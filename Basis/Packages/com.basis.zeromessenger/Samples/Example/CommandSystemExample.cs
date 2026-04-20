using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.ZeroMessenger.Samples
{
    /// <summary>
    /// Advanced example showing command pattern with undo/redo using ZeroMessenger
    /// </summary>
    public class CommandSystemExample : MonoBehaviour
    {
        private MessageBroker<ICommand> commandBroker;
        private Stack<ICommand> undoStack = new Stack<ICommand>();
        private Stack<ICommand> redoStack = new Stack<ICommand>();

        void Start()
        {
            commandBroker = new MessageBroker<ICommand>();

            // Subscribe to command execution
            commandBroker.SubscribeAwait(async (cmd, ct) =>
            {
                await cmd.ExecuteAsync(ct);
                undoStack.Push(cmd);
                redoStack.Clear();
                BasisDebug.Log($"Executed: {cmd.GetType().Name}");
            });

            // Example commands
            ExecuteCommand(new MovePlayerCommand(new Vector3(10, 0, 0)));
            ExecuteCommand(new DealDamageCommand(targetId: 123, damage: 50));
        }

        void ExecuteCommand(ICommand command)
        {
            commandBroker.Publish(command);
        }

        void Undo()
        {
            if (undoStack.Count > 0)
            {
                var command = undoStack.Pop();
                command.Undo();
                redoStack.Push(command);
                BasisDebug.Log($"Undid: {command.GetType().Name}");
            }
        }

        void Redo()
        {
            if (redoStack.Count > 0)
            {
                var command = redoStack.Pop();
                commandBroker.Publish(command);
            }
        }

        void OnDestroy()
        {
            commandBroker?.Dispose();
        }
    }

    public interface ICommand
    {
        ValueTask ExecuteAsync(CancellationToken ct);
        void Undo();
    }

    public class MovePlayerCommand : ICommand
    {
        private readonly Vector3 movement;
        private Vector3 previousPosition;

        public MovePlayerCommand(Vector3 movement)
        {
            this.movement = movement;
        }

        public async ValueTask ExecuteAsync(CancellationToken ct)
        {
            // Simulate getting current position
            previousPosition = Vector3.zero; // In real code, get from player
            // Apply movement
            await Task.Delay(10, ct);
            BasisDebug.Log($"Moved player by {movement}");
        }

        public void Undo()
        {
            BasisDebug.Log($"Undoing move, restoring position to {previousPosition}");
        }
    }

    public class DealDamageCommand : ICommand
    {
        private readonly int targetId;
        private readonly int damage;

        public DealDamageCommand(int targetId, int damage)
        {
            this.targetId = targetId;
            this.damage = damage;
        }

        public async ValueTask ExecuteAsync(CancellationToken ct)
        {
            await Task.Delay(10, ct);
            BasisDebug.Log($"Dealt {damage} damage to target {targetId}");
        }

        public void Undo()
        {
            BasisDebug.Log($"Undoing damage, restoring {damage} health to target {targetId}");
        }
    }
}