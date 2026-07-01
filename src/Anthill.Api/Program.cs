// ANTHILL secured local API + colony UI (v1.8.6) entry point.
// The host is built in ApiHost so the CLI's `--api` path can launch the identical
// server without duplicating the security bootstrap.
return Anthill.Api.ApiHost.Run(args);
