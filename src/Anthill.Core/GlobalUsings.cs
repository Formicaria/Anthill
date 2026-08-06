// The domain entity is named Task (faithful to the Python model). Under implicit usings
// that clashes with System.Threading.Tasks.Task, so we bind the bare identifier `Task` to
// the domain type across Anthill.Core. The handful of places that need the threading type
// (the parallel mission executor) reference System.Threading.Tasks.Task fully qualified.
global using Task = Anthill.Core.Domain.Task;
// .NET 9 implicit usings pull in System.Threading.Tasks.*, causing ambiguity with the
// Anthill-domain types of the same name. Pin them globally so every file gets the right one.
global using TaskScheduler = Anthill.Core.Scheduling.TaskScheduler;
global using TaskStatus = Anthill.Core.Domain.TaskStatus;

// v3.8.4 — the reasoning protocol moved from Anthill.Core.Models to Anthill.SDK.Reasoning:
// ModelRequest/ModelResponse/ModelMessage, ModelCallOutcome, ModelCapabilities, ProviderCatalog,
// ModelCallScope, and IReasoningProvider.
//
// Global rather than an added `using` in each of the ~20 affected files. The types did not change,
// their members did not change, and no call site's MEANING changed — only which assembly declares
// them. Touching twenty files to say so would put real edits and mechanical ones in the same diff,
// and the review that matters here is "did anything move that shouldn't have", which a diff full of
// import churn actively hides.
global using Anthill.SDK.Reasoning;
