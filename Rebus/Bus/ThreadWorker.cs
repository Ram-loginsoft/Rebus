﻿using System;
using System.Linq;
using System.Threading;
using Rebus.Logging;
using Rebus.Pipeline;
using Rebus.Transport;

namespace Rebus.Bus
{
    /// <summary>
    /// Implementation of <see cref="IWorker"/> that has a dedicated thread the continuously polls the given <see cref="ThreadWorkerSynchronizationContext"/> for work,
    /// and in case it doesn't find any, it'll try to receive a new message and invoke a receive pipeline on that
    /// </summary>
    public class ThreadWorker : IWorker
    {
        static ILog _log;

        static ThreadWorker()
        {
            RebusLoggerFactory.Changed += f => _log = f.GetCurrentClassLogger();
        }

        readonly ThreadWorkerSynchronizationContext _threadWorkerSynchronizationContext;
        readonly int _maxParallelismPerWorker;
        readonly ITransport _transport;
        readonly IPipeline _pipeline;
        readonly Thread _workerThread;
        readonly IPipelineInvoker _pipelineInvoker;

        volatile bool _keepWorking = true;

        public ThreadWorker(ITransport transport, IPipeline pipeline, IPipelineInvoker pipelineInvoker, string workerName, ThreadWorkerSynchronizationContext threadWorkerSynchronizationContext, int maxParallelismPerWorker)
        {
            Name = workerName;

            _transport = transport;
            _pipeline = pipeline;
            _pipelineInvoker = pipelineInvoker;
            _threadWorkerSynchronizationContext = threadWorkerSynchronizationContext;
            _maxParallelismPerWorker = maxParallelismPerWorker;
            _workerThread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(_threadWorkerSynchronizationContext);
                _log.Debug("Starting worker {0}", Name);
                while (_keepWorking)
                {
                    DoWork();
                }
                _log.Debug("Worker {0} stopped", Name);
            })
            {
                Name = workerName
            };
            _workerThread.Start();
        }

        void DoWork()
        {
            try
            {
                var nextContinuationOrNull = _threadWorkerSynchronizationContext.GetNextContinuationOrNull();

                if (nextContinuationOrNull != null)
                {
                    nextContinuationOrNull();
                    return;
                }

                TryProcessMessage();
            }
            catch (ThreadAbortException)
            {
                _log.Debug("Aborting worker {0}", Name);
                _keepWorking = false;
            }
            catch (Exception exception)
            {
                _log.Error(exception, "Error while attempting to do work");
            }
        }

        int _continuationsWaitingToBePosted;

        async void TryProcessMessage()
        {
            if (_continuationsWaitingToBePosted >= _maxParallelismPerWorker)
            {
                Thread.Sleep(100);
                return;
            }

            _continuationsWaitingToBePosted++;

            using (var transactionContext = new DefaultTransactionContext())
            {
                try
                {
                    AmbientTransactionContext.Current = transactionContext;

                    var message = await _transport.Receive(transactionContext);

                    if (message == null) return;

                    var context = new IncomingStepContext(message, transactionContext);
                    transactionContext.Items[StepContext.StepContextKey] = context;

                    var stagedReceiveSteps = _pipeline.ReceivePipeline();

                    await _pipelineInvoker.Invoke(context, stagedReceiveSteps.Select(s => s.Step));

                    transactionContext.Complete();
                }
                catch (Exception exception)
                {
                    _log.Error(exception, "Unhandled exception in thread worker");
                }
                finally
                {
                    AmbientTransactionContext.Current = null;
                    _continuationsWaitingToBePosted--;
                }
            }
        }

        public string Name { get; private set; }

        public void Stop()
        {
            _keepWorking = false;
        }

        public void Dispose()
        {
            _keepWorking = false;
            _workerThread.Join();
        }
    }
}