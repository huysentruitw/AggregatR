using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Aggregator.Exceptions;
using Aggregator.Internal;
using Aggregator.Persistence;

namespace Aggregator.Command
{
    /// <summary>
    /// This class is responsibly for processing commands. Should be used as a singleton.
    /// </summary>
    /// <typeparam name="TIdentifier">The identifier type.</typeparam>
    /// <typeparam name="TCommandBase">The command base type.</typeparam>
    /// <typeparam name="TEventBase">The event base type.</typeparam>
    public sealed class CommandProcessor<TIdentifier, TCommandBase, TEventBase>
        where TIdentifier : IEquatable<TIdentifier>
    {
        private readonly ConcurrentDictionary<Type, MethodInfo> _executeMethodCache = new ConcurrentDictionary<Type, MethodInfo>();
        private readonly ICommandHandlingScopeFactory _commandHandlingScopeFactory;
        private readonly IEventStore<TIdentifier, TEventBase> _eventStore;

        /// <summary>
        /// Constructs a new <see cref="CommandProcessor{TIdentifier, TCommandBase, TEventBase}"/> instance.
        /// </summary>
        /// <param name="commandHandlingScopeFactory">The command handling scope factory.</param>
        /// <param name="eventStore">The event store.</param>
        public CommandProcessor(
            ICommandHandlingScopeFactory commandHandlingScopeFactory,
            IEventStore<TIdentifier, TEventBase> eventStore)
        {
            _commandHandlingScopeFactory = commandHandlingScopeFactory ?? throw new ArgumentNullException(nameof(commandHandlingScopeFactory));
            _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        }

        /// <summary>
        /// Processes a single command.
        /// </summary>
        /// <param name="command">The command to process.</param>
        /// <returns>An awaitable <see cref="Task"/>.</returns>
        public async Task Process(TCommandBase command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            var context = new CommandHandlingContext();

            var unitOfWork = new UnitOfWork<TIdentifier, TEventBase>();
            context.SetUnitOfWork(unitOfWork);

            var executeMethod = _executeMethodCache.GetOrAdd(command.GetType(), type =>
                GetType()
                    .GetMethod(nameof(Execute), BindingFlags.NonPublic | BindingFlags.Instance)
                    .MakeGenericMethod(type));

            await ((Task)executeMethod.Invoke(this, new object[] { command, context })).ConfigureAwait(false);

            if (!unitOfWork.HasChanges) return;
        }

        private async Task Execute<TCommand>(TCommand command, CommandHandlingContext context)
            where TCommand : TCommandBase
        {
            using (var commandHandlingScope = _commandHandlingScopeFactory.BeginScopeFor<TCommand>(context))
            {
                var handlers = commandHandlingScope.ResolveHandlers();

                if (handlers == null || !handlers.Any())
                    throw new UnhandledCommandException(command);

                foreach (var handler in handlers)
                    await handler.Handle(command).ConfigureAwait(false);
            }
        }
    }
}
