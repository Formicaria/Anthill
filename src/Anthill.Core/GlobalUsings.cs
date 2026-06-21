// The domain entity is named Task (faithful to the Python model). Under implicit usings
// that clashes with System.Threading.Tasks.Task, so we bind the bare identifier `Task` to
// the domain type across Anthill.Core. The handful of places that need the threading type
// (the parallel mission executor) reference System.Threading.Tasks.Task fully qualified.
global using Task = Anthill.Core.Domain.Task;
// .NET 9 implicit usings pull in System.Threading.Tasks.*, causing ambiguity with the
// Anthill-domain types of the same name. Pin them globally so every file gets the right one.
global using TaskScheduler = Anthill.Core.Scheduling.TaskScheduler;
global using TaskStatus = Anthill.Core.Domain.TaskStatus;
