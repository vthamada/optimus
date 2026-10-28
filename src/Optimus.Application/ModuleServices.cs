namespace Optimus.Application;

public sealed record ModuleServices(IAssistantService Assistant, IBackupService Backup, ISmartService Smart);
