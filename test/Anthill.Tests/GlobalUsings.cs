// Match Anthill.Core: bind the bare `Task` identifier to the domain entity so test code
// reads naturally (new Task { ... }) without colliding with System.Threading.Tasks.Task.
global using Task = Anthill.Core.Domain.Task;
