/*
 * Copyright 2023 Dgraph Labs, Inc. and Contributors
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using FluentResults;
using Grpc.Core;

namespace Dgraph.Transactions;

internal abstract class TransactionBase : IQuery
{
    TransactionState IQuery.TransactionState => TransactionState;
    protected internal TransactionState TransactionState;
    protected internal readonly IDgraphClientInternal Client;
    protected internal readonly Api.TxnContext Context;
    protected internal readonly bool ReadOnly;
    protected internal readonly bool BestEffort;

    protected internal TransactionBase(IDgraphClientInternal client, bool readOnly, bool bestEffort)
    {
        Client = client;
        ReadOnly = readOnly;
        BestEffort = bestEffort;
        TransactionState = TransactionState.OK;
        Context = new Api.TxnContext();
    }

    async Task<Result<Response>> IQuery.QueryWithVars(
        string queryString,
        Dictionary<string, string> varMap,
        CallOptions? options
    )
    {
        AssertNotDisposed();

        if (TransactionState != TransactionState.OK)
        {
            return Result.Fail<Response>(
                new TransactionNotOK(TransactionState.ToString()));
        }

        try
        {
            Api.Request request = new Api.Request
            {
                Query = queryString,
                StartTs = Context.StartTs,
                Hash = Context.Hash,
                ReadOnly = ReadOnly,
                BestEffort = BestEffort
            };
            request.Vars.Add(varMap);

            var response = await Client.DgraphExecute(
                async (dg) =>
                    Result.Ok<Response>(
                        new Response(await dg.QueryAsync(
                            request,
                            options ?? new CallOptions())
                    )),
                (rpcEx) => Result.Fail<Response>(new ExceptionalError(rpcEx))
            );

            if (response.IsFailed)
            {
                return response;
            }

            var err = MergeContext(response.Value.DgraphResponse.Txn);

            if (err.IsSuccess)
            {
                return response;
            }
            else
            {
                return err.ToResult<Response>();
            }

        }
        catch (Exception ex)
        {
            return Result.Fail<Response>(new ExceptionalError(ex));
        }
    }

    protected Result MergeContext(Api.TxnContext srcContext)
    {
        if (srcContext == null)
        {
            return Result.Ok();
        }

        if (Context.StartTs == 0)
        {
            Context.StartTs = srcContext.StartTs;
        }

        if (Context.StartTs != srcContext.StartTs)
        {
            return Result.Fail(new StartTsMismatch());
        }

        Context.Hash = srcContext.Hash;

        Context.Keys.Add(srcContext.Keys);
        Context.Preds.Add(srcContext.Preds);

        return Result.Ok();
    }

    protected internal virtual void AssertNotDisposed() { }
}

