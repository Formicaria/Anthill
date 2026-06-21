// Match Anthill.Core: bind the bare `Task` identifier to the domain entity so test code
// reads naturally (new Task { ... }) without colliding with System.Threading.Tasks.Task.
global using Task = Anthill.Core.Domain.Task;
// .NET 9 implicit usings expose System.Threading.Tasks.TaskScheduler and TaskStatus — pin
// the Anthill types globally so all test files resolve to the correct symbols.
global using TaskScheduler = Anthill.Core.Scheduling.TaskScheduler;
global using TaskStatus = Anthill.Core.Domain.TaskStatus;
