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

internal sealed class Transaction : TransactionBase, ITransaction
{
    private bool HasMutated;

    internal Transaction(IDgraphClientInternal client) : base(client, false, false) { }

    async Task<Result<Response>> ITransaction.Do(Api.Request request, CallOptions? options)
    {
        AssertNotDisposed();

        if (string.IsNullOrWhiteSpace(request.Query) && request.Mutations.Count == 0)
        {
            return Result.Ok(new Response(new Api.Response()));
        }

        if (TransactionState != TransactionState.OK)
        {
            return Result.Fail<Response>(new TransactionNotOK(TransactionState.ToString()));
        }

        HasMutated = true;

        request.StartTs = Context.StartTs;
        request.Hash = Context.Hash;
        request.BestEffort = BestEffort;
        request.ReadOnly = ReadOnly;

        var response = await Client.DgraphExecute(
            async (dg) => Result.Ok<Response>(new Response(await dg.QueryAsync(request, options ?? new CallOptions()))),
            (rpcEx) => Result.Fail<Response>(new ExceptionalError(rpcEx))
        );

        if (response.IsFailed)
        {
            await (this as ITransaction).Discard(); // Ignore error - user should see the original error.

            TransactionState = TransactionState.Error; // overwrite the aborted value
            return response;
        }

        if (request.CommitNow)
        {
            TransactionState = TransactionState.Committed;
        }

        var err = MergeContext(response.Value.DgraphResponse.Txn);
        if (err.IsFailed)
        {
            // The WithErrors() here will turn this Ok, into a Fail.  So the result 
            // and an error are in there like the Go lib.  But this can really only
            // occur on an internal Dgraph error, so it's really an error
            // and there's no need to code for cases to dig out the value and the 
            // error - just 
            //   if (...IsFailed) { ...assume mutation failed...}
            // is enough.
            return Result.Ok<Response>(response.Value).WithErrors(err.Errors);
        }

        return Result.Ok<Response>(response.Value);
    }

    // Dispose method - Must be ok to call multiple times!
    async Task<Result> ITransaction.Discard(CallOptions? options)
    {
        if (TransactionState != TransactionState.OK)
        {
            // TransactionState.Committed can't be discarded
            // TransactionState.Error only entered after Discard() is already called.
            // TransactionState.Aborted multiple Discards have no effect
            return Result.Ok();
        }

        TransactionState = TransactionState.Aborted;

        if (!HasMutated)
        {
            return Result.Ok();
        }

        Context.Aborted = true;

        return await Client.DgraphExecute(
            async (dg) =>
            {
                await dg.CommitOrAbortAsync(
                    Context,
                    options ?? new CallOptions());
                return Result.Ok();
            },
            (rpcEx) => Result.Fail(new ExceptionalError(rpcEx))
        );
    }

    async Task<Result> ITransaction.Commit(CallOptions? options)
    {
        AssertNotDisposed();

        if (TransactionState != TransactionState.OK)
        {
            return Result.Fail(new TransactionNotOK(TransactionState.ToString()));
        }

        TransactionState = TransactionState.Committed;

        if (!HasMutated)
        {
            return Result.Ok();
        }

        return await Client.DgraphExecute(
            async (dg) =>
            {
                await dg.CommitOrAbortAsync(
                    Context,
                    options ?? new CallOptions());
                return Result.Ok();
            },
            (rpcEx) => Result.Fail(new ExceptionalError(rpcEx))
        );
    }

    // 
    // ------------------------------------------------------
    //              disposable pattern.
    // ------------------------------------------------------
    //
    #region disposable pattern

    private bool Disposed;

    protected internal override void AssertNotDisposed()
    {
        if (Disposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }

    void IDisposable.Dispose()
    {
        if (!Disposed && TransactionState == TransactionState.OK)
        {
            Disposed = true;

            // This makes Discard run async (maybe another thread)  So the current thread 
            // might exit and get back to work (we don't really care how the Discard() went).
            // But, this could race with disposal of everything, if this disposal is running
            // with whole program shutdown.  I don't think this matters because Dgraph will
            // clean up the transaction at some point anyway and if we've exited the program, 
            // we don't care.
            Task.Run(() => (this as ITransaction).Discard());
        }
    }

    #endregion
}
