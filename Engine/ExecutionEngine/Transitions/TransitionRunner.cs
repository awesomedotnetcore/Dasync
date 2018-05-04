﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Dasync.Accessors;
using Dasync.AsyncStateMachine;
using Dasync.EETypes.Descriptors;
using Dasync.EETypes.Intents;
using Dasync.EETypes.Proxy;
using Dasync.EETypes.Transitions;
using Dasync.ExecutionEngine.Extensions;
using Dasync.ExecutionEngine.IntrinsicFlow;
using Dasync.ExecutionEngine.StateMetadata.Service;
using Dasync.Proxy;
using Dasync.ValueContainer;

namespace Dasync.ExecutionEngine.Transitions
{
    public class TransitionRunner : ITransitionRunner
    {
        private readonly ITransitionScope _transitionScope;
        private readonly ITransitionCommitter _transitionCommitter;
        private readonly IServiceProxyBuilder _serviceProxyBuilder;
        private readonly IRoutineMethodResolver _routineMethodResolver;
        private readonly IAsyncStateMachineMetadataProvider _asyncStateMachineMetadataProvider;
        private readonly IMethodInvokerFactory _methodInvokerFactory;
        private readonly IServiceStateValueContainerProvider _serviceStateValueContainerProvider;
        private readonly IntrinsicRoutines _intrinsicRoutines;

        public TransitionRunner(
            ITransitionScope transitionScope,
            ITransitionCommitter transitionCommitter,
            IServiceProxyBuilder serviceProxyBuilder,
            IRoutineMethodResolver routineMethodResolver,
            IAsyncStateMachineMetadataProvider asyncStateMachineMetadataProvider,
            IMethodInvokerFactory methodInvokerFactory,
            IServiceStateValueContainerProvider serviceStateValueContainerProvider,
            IntrinsicRoutines intrinsicRoutines)
        {
            _transitionScope = transitionScope;
            _transitionCommitter = transitionCommitter;
            _serviceProxyBuilder = serviceProxyBuilder;
            _routineMethodResolver = routineMethodResolver;
            _asyncStateMachineMetadataProvider = asyncStateMachineMetadataProvider;
            _methodInvokerFactory = methodInvokerFactory;
            _serviceStateValueContainerProvider = serviceStateValueContainerProvider;
            _intrinsicRoutines = intrinsicRoutines;
        }

        public async Task RunAsync(
            ITransitionCarrier transitionCarrier,
            ITransitionData transitionData,
            CancellationToken ct)
        {
            var transitionDescriptor = await transitionData.GetTransitionDescriptorAsync(ct);

            if (transitionDescriptor.Type == TransitionType.InvokeRoutine ||
                transitionDescriptor.Type == TransitionType.ContinueRoutine)
            {
                await RunRoutineAsync(transitionCarrier, transitionData, transitionDescriptor, ct);
            }
            else
            {
                throw new InvalidOperationException();
            }
        }

        private async Task RunRoutineAsync(
            ITransitionCarrier transitionCarrier,
            ITransitionData transitionData,
            TransitionDescriptor transitionDescriptor,
            CancellationToken ct)
        {
            using (_transitionScope.Enter(transitionDescriptor))
            {
                var transitionMonitor = _transitionScope.CurrentMonitor;

                var serviceId = await transitionData.GetServiceIdAsync(ct);
                var routineDescriptor = await transitionData.GetRoutineDescriptorAsync(ct);

                var serviceInstance =
#warning IntrinsicRoutines must be registered in the service registry, but it needs the engine IoC to resolve.
                    serviceId.ServiceName == nameof(IntrinsicRoutines)
                    ? _intrinsicRoutines
                    : _serviceProxyBuilder.Build(serviceId);
#warning check if the serviceInstance proxy is an actual non-abstract class with implementation

                // Need exact underlying type of the service implementation type to call
                // the routine method directly without using the virtual method table.
                var serviceType = (serviceInstance as IProxy)?.ObjectType ?? serviceInstance.GetType();
                var routineMethod = _routineMethodResolver.Resolve(serviceType, routineDescriptor.MethodId);

                var serviceStateContainer = _serviceStateValueContainerProvider.CreateContainer(serviceInstance);
                var isStatefullService = serviceStateContainer.GetCount() > 0;
                if (isStatefullService)
                    await transitionData.ReadServiceStateAsync(serviceStateContainer, ct);

                Task completionTask;
                IValueContainer asmValueContainer = null;

                if (TryCreateAsyncStateMachine(routineMethod, out var asmInstance, out var asmMetadata, out completionTask))
                {
                    var isContinuation = transitionDescriptor.Type == TransitionType.ContinueRoutine;
                    asmValueContainer = await LoadRoutineStateAsync(transitionData, asmInstance, asmMetadata, isContinuation, ct);

                    asmMetadata.Owner.FieldInfo?.SetValue(asmInstance, serviceInstance);

                    transitionMonitor.OnRoutineStart(
                        serviceId,
                        routineDescriptor,
                        serviceInstance,
                        routineMethod,
                        asmInstance);

                    try
                    {
#warning possibly need to create a proxy? on a sealed ASM class? How to capture Task.Delay if it's not immediate after first MoveNext?
                        asmInstance.MoveNext();
                    }
                    catch (Exception ex)
                    {
                        // The MoveNext() must not throw, but instead complete the task with an error.
                        // try-catch is added just in case for a non-compiler-generated state machine.
                        var taskResultType = TaskAccessor.GetTaskResultType(routineMethod.ReturnType);
                        completionTask = TaskAccessor.FromException(taskResultType, ex);
                    }
                }
                else
                {
                    if (transitionDescriptor.Type == TransitionType.ContinueRoutine)
                        throw new InvalidOperationException("Cannot continue a routine because it's not a state machine.");

                    var methodInvoker = _methodInvokerFactory.Create(routineMethod);
                    var parameters = await LoadMethodParametersAsync(transitionData, methodInvoker, ct);

                    transitionMonitor.OnRoutineStart(
                        serviceId,
                        routineDescriptor,
                        serviceInstance,
                        routineMethod,
                        routineStateMachine: null);

                    try
                    {
                        completionTask = methodInvoker.Invoke(serviceInstance, parameters);
                    }
                    catch (Exception ex)
                    {
#warning IDisposable.Dispose returns void, not a Task
                        var taskResultType = TaskAccessor.GetTaskResultType(routineMethod.ReturnType);
                        completionTask = TaskAccessor.FromException(taskResultType, ex);
                    }
                }

                if (completionTask == null)
                {
#warning Check if this method is really IDiposable.Dispose() ?
                    if (routineMethod.Name == "Dispose")
                    {
                        completionTask = TaskAccessor.CompletedTask;
                    }
                    else
                    {
                        // This is possible if the routine is not marked as 'async' and just returns a NULL result.
                        throw new Exception("Critical: a routine method returned null task");
                    }
                }

                var scheduledActions = await transitionMonitor.TrackRoutineCompletion(completionTask);

                if (scheduledActions.SaveRoutineState || isStatefullService)
                {
                    scheduledActions.SaveStateIntent = new SaveStateIntent
                    {
                        ServiceId = serviceId,
                        ServiceState = isStatefullService ? serviceStateContainer : null,
                        Routine = scheduledActions.SaveRoutineState ? routineDescriptor : null,
                        RoutineState = scheduledActions.SaveRoutineState ? asmValueContainer : null,
                        AwaitingRoutine = scheduledActions.ExecuteRoutineIntents?.SingleOrDefault(
                            intent => intent.Continuation?.Routine?.IntentId == routineDescriptor.IntentId)
                    };
                }

                if (scheduledActions.ExecuteRoutineIntents?.Count > 0)
                {
                    var callerDescriptor = new CallerDescriptor
                    {
                        ServiceId = serviceId,
                        Routine = routineDescriptor
                    };

                    foreach (var intent in scheduledActions.ExecuteRoutineIntents)
                        intent.Caller = callerDescriptor;
                }

                if (completionTask.IsCompleted)
                {
                    var routineResult = completionTask.ToTaskResult();

                    scheduledActions.SaveStateIntent.RoutineResult = routineResult;

                    var awaitedResultDescriptor = new RoutineResultDescriptor
                    {
                        Result = routineResult,
                        IntentId = routineDescriptor.IntentId
                    };

                    var awaitedRoutineDescriptor = new CallerDescriptor
                    {
                        ServiceId = serviceId,
                        Routine = routineDescriptor
                    };

                    await AddContinuationIntentsAsync(
                        transitionData,
                        scheduledActions,
                        awaitedResultDescriptor,
                        awaitedRoutineDescriptor,
                        ct);
                }

                await _transitionCommitter.CommitAsync(transitionCarrier, scheduledActions, ct);
            }
        }

        private async Task<IValueContainer> LoadMethodParametersAsync(
            ITransitionData transitionData,
            IMethodInvoker methodInvoker,
            CancellationToken ct)
        {
            var valueContainer = methodInvoker.CreateParametersContainer();
            await transitionData.ReadRoutineParametersAsync(valueContainer, ct);
            return valueContainer;
        }

        private bool TryCreateAsyncStateMachine(
            MethodInfo methodInfo,
            out IAsyncStateMachine asyncStateMachine,
            out AsyncStateMachineMetadata metadata,
            out Task completionTask)
        {
            if (!methodInfo.IsAsyncStateMachine())
            {
                asyncStateMachine = null;
                metadata = null;
                completionTask = null;
                return false;
            }

            metadata = _asyncStateMachineMetadataProvider.GetMetadata(methodInfo);
            asyncStateMachine = (IAsyncStateMachine)Activator.CreateInstance(metadata.StateMachineType);

            metadata.State.FieldInfo?.SetValue(asyncStateMachine, -1);

            var createBuilderMethod = metadata.Builder.FieldInfo.FieldType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
            var taskBuilder = createBuilderMethod.Invoke(null, null);
            var taskField = metadata.Builder.FieldInfo.FieldType.GetProperty("Task");
            completionTask = (Task)taskField.GetValue(taskBuilder); // builder is a struct, need to initialize the Task here!
            metadata.Builder.FieldInfo.SetValue(asyncStateMachine, taskBuilder);

            return true;
        }

        private async Task<IValueContainer> LoadRoutineStateAsync(
            ITransitionData transitionData,
            IAsyncStateMachine asyncStateMachine,
            AsyncStateMachineMetadata metadata,
            bool isContinuation,
            CancellationToken ct)
        {
            var asmValueContainer = GetValueContainerProxy(asyncStateMachine, metadata);

            if (isContinuation)
            {
                var awaitedResult = await transitionData.GetAwaitedResultAsync(ct);
                if (awaitedResult != null)
                    TaskCapture.StartCapturing();

                await transitionData.ReadRoutineStateAsync(asmValueContainer, ct);

                if (awaitedResult != null)
                    UpdateTasksWithAwaitedRoutineResult(
                        TaskCapture.FinishCapturing(), awaitedResult);
            }
            else
            {
                await transitionData.ReadRoutineParametersAsync(asmValueContainer, ct);
            }

            return asmValueContainer;
        }

        private static void UpdateTasksWithAwaitedRoutineResult(
            List<Task> deserializedTasks, RoutineResultDescriptor awaitedResult)
        {
            foreach (var task in deserializedTasks)
            {
                if (task.AsyncState is ProxyTaskState state &&
                    state.IntentId == awaitedResult.IntentId)
                {
                    task.TrySetResult(awaitedResult.Result);
                }
            }
        }

        private static IValueContainer GetValueContainerProxy(
            IAsyncStateMachine asyncStateMachine,
            AsyncStateMachineMetadata metadata)
        {
            var allFields = GetFields(metadata);
            var fieldDescs = allFields.Select(arg => new KeyValuePair<string, MemberInfo>(
                string.IsNullOrEmpty(arg.Name) ? arg.InternalName : arg.Name, arg.FieldInfo));
            return ValueContainerFactory.CreateProxy(asyncStateMachine, fieldDescs);
        }

        private static IEnumerable<AsyncStateMachineField> GetFields(AsyncStateMachineMetadata metadata)
        {
            if (metadata.State.FieldInfo != null)
                yield return metadata.State;

            if (metadata.InputArguments != null)
                foreach (var field in metadata.InputArguments)
                    yield return field;

            if (metadata.LocalVariables != null)
                foreach (var field in metadata.LocalVariables)
                    yield return field;
        }

        private async Task AddContinuationIntentsAsync(
            ITransitionData transitionData,
            ScheduledActions actions,
            RoutineResultDescriptor awaitedResultDescriptor,
            CallerDescriptor awaitedRoutineDescriptor,
            CancellationToken ct)
        {
            var continuations = await transitionData.GetContinuationsAsync(ct);
            if (continuations?.Count > 0)
            {
                actions.ContinuationIntents = new List<ContinueRoutineIntent>(continuations.Count);
                foreach (var continuation in continuations)
                {
                    var intent = new ContinueRoutineIntent
                    {
                        Continuation = continuation,
                        Result = awaitedResultDescriptor,
                        Callee = awaitedRoutineDescriptor
                    };
                    actions.ContinuationIntents.Add(intent);
                }
            }
        }
    }
}