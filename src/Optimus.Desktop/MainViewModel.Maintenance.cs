using System.Collections.ObjectModel;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Desktop;

public sealed record IntegrityChoice(IntegrityOperation Operation, string Label);
public sealed record MaintenanceQueryChoice(MaintenanceQuery Query, string Label, string Description);

public sealed partial class MainViewModel
{
    private MaintenanceServices? _maintenance;
    private IntegrityPlan? _integrityPlan;
    private IntegrityChoice _integrityChoice = null!;
    private long _integrityRevision;
    private string _integrityPreview = "Escolha a verificação e prepare a prévia. Nenhum reparo será executado.";
    private string _integrityResult = "Nenhuma verificação executada nesta sessão.";
    private string _folderSpacePath = "";
    private MaintenanceQueryChoice _selectedMaintenanceQuery = null!;

    public IReadOnlyList<MaintenanceQueryChoice> MaintenanceQueryChoices { get; } = Array.AsReadOnly(new[]
    {
        new MaintenanceQueryChoice(MaintenanceQuery.Defender, "Microsoft Defender", "Consulte os estados e assinaturas reportados, sem iniciar varredura ou alterar proteções."),
        new MaintenanceQueryChoice(MaintenanceQuery.DeviceDrivers, "Dispositivos e drivers", "Consulte dispositivos e drivers registrados, sem instalar ou habilitar componentes."),
        new MaintenanceQueryChoice(MaintenanceQuery.Services, "Serviços do Windows", "Consulte estado e configuração reportada dos serviços, sem iniciar, parar ou reconfigurar."),
        new MaintenanceQueryChoice(MaintenanceQuery.Updates, "Histórico de atualizações", "Consulte o histórico local WUA e seu sinal de reinício. A consulta não procura nem instala atualizações."),
        new MaintenanceQueryChoice(MaintenanceQuery.Processes, "Processos e memória", "Consulte um recorte de processos e memória reportada, sem encerrar processos ou ler seu conteúdo."),
        new MaintenanceQueryChoice(MaintenanceQuery.ScheduledTasks, "Tarefas agendadas", "Consulte metadados das tarefas acessíveis, sem executar ou modificar tarefas."),
        new MaintenanceQueryChoice(MaintenanceQuery.Storage, "Discos e partições", "Consulte capacidades e identificadores reportados. Não realiza SMART, teste físico ou alteração de disco."),
        new MaintenanceQueryChoice(MaintenanceQuery.Connections, "Conexões e portas TCP/UDP", "Consulte conexões e portas locais reportadas, sem enviar pacotes, resolver nomes ou encerrar conexões."),
        new MaintenanceQueryChoice(MaintenanceQuery.Firewall, "Perfis do Firewall", "Consulte perfis da política ActiveStore, sem alterar o Firewall. Os perfis não comprovam filtragem de todo tráfego."),
        new MaintenanceQueryChoice(MaintenanceQuery.ApplicationFailures, "Falhas de aplicativos", "Consulte eventos selecionados do log Application nos últimos sete dias, preservando fonte, data e XML original."),
        new MaintenanceQueryChoice(MaintenanceQuery.PendingRestart, "Indicadores de reinicialização", "Consulte indicadores selecionados no Registro. A ausência deles não comprova que o Windows dispense reinicialização."),
        new MaintenanceQueryChoice(MaintenanceQuery.RestorePoints, "Pontos de restauração", "Consulte os pontos reportados pelo Windows. A lista não comprova que uma restauração funcionará; nenhum ponto será criado ou alterado."),
        new MaintenanceQueryChoice(MaintenanceQuery.BitLocker, "Estado do BitLocker", "Consulte os estados de criptografia e proteção reportados, sem coletar chaves de recuperação ou alterar os volumes."),
        new MaintenanceQueryChoice(MaintenanceQuery.RestartHistory, "Histórico de reinicializações", "Consulte registros selecionados dos últimos 30 dias, sem somar 41 e 6008 como dois reinícios. Use a aba Instabilidade para escolher o período."),
        new MaintenanceQueryChoice(MaintenanceQuery.MemoryResults, "Resultados de diagnóstico de memória", "Consulte resultados já registrados nos últimos 30 dias. Esta consulta não executa nem agenda testes."),
        new MaintenanceQueryChoice(MaintenanceQuery.HardwareProfile, "Placa-mãe, BIOS e memória", "Consulte identificadores e valores SMBIOS reportados, sem inferir XMP/EXPO ou modificar firmware.")
    });
    public MaintenanceQueryChoice SelectedMaintenanceQuery
    {
        get => _selectedMaintenanceQuery;
        set
        {
            if (value is null || !MaintenanceQueryChoices.Contains(value) || value == _selectedMaintenanceQuery) return;
            _selectedMaintenanceQuery = value;
            Changed(); Changed(nameof(MaintenanceQueryDescription)); Changed(nameof(HasVisibleMaintenanceSelection)); RunMaintenanceQueryCommand?.Notify();
        }
    }
    public string MaintenanceQueryDescription => SelectedMaintenanceQuery.Description;
    public AsyncCommand RunMaintenanceQueryCommand { get; private set; } = null!;

    public ObservableCollection<DiagnosticEvidence> MaintenanceEvidence { get; } = [];
    public IReadOnlyList<IntegrityChoice> IntegrityChoices { get; } = Array.AsReadOnly(new[]
    {
        new IntegrityChoice(IntegrityOperation.SfcVerifyOnly, "SFC · verificar arquivos protegidos"),
        new IntegrityChoice(IntegrityOperation.DismCheckHealth, "DISM · consultar sinalização de integridade"),
        new IntegrityChoice(IntegrityOperation.DismScanHealth, "DISM · examinar repositório de componentes")
    });
    public AsyncCommand FolderSpaceCommand { get; private set; } = null!;
    public ActionCommand BrowseFolderSpaceCommand { get; private set; } = null!;
    public string FolderSpacePath
    {
        get => _folderSpacePath;
        set { if (_folderSpacePath == value) return; _folderSpacePath = value; Changed(); FolderSpaceCommand?.Notify(); }
    }
    public AsyncCommand PlanIntegrityCommand { get; private set; } = null!;
    public AsyncCommand ExecuteIntegrityCommand { get; private set; } = null!;
    public string IntegrityPreview { get => _integrityPreview; private set { _integrityPreview = value; Changed(); } }
    public string IntegrityResult { get => _integrityResult; private set { _integrityResult = value; Changed(); } }
    public bool IntegrityHasPreparedPlan => _integrityPlan is not null;
    public IntegrityChoice SelectedIntegrityChoice
    {
        get => _integrityChoice;
        set
        {
            if (value is null || value == _integrityChoice) return;
            _integrityChoice = value;
            _integrityRevision++;
            _integrityPlan = null;
            IntegrityPreview = "A seleção mudou. Prepare outra prévia antes de executar.";
            Changed();
            RefreshMaintenanceCommands();
        }
    }

    private void InitializeMaintenance(MaintenanceServices? services)
    {
        _maintenance = services;
        _integrityChoice = IntegrityChoices[0];
        _selectedMaintenanceQuery = MaintenanceQueryChoices[0];
        RunMaintenanceQueryCommand = new(async () => { await RunSelectedMaintenanceQueryAsync(); },
            () => !IsBusy && HasVisibleMaintenanceSelection && _maintenance?.ResolveQuery(SelectedMaintenanceQuery.Query).IsSuccess == true, SetError);
        FolderSpaceCommand = new(async () => { await RunFolderSpaceAsync(); }, () => !IsBusy && _maintenance?.FolderSpace is not null && !string.IsNullOrWhiteSpace(FolderSpacePath), SetError);
        BrowseFolderSpaceCommand = new(() => PickFolder("Selecionar pasta para análise de espaço", value => FolderSpacePath = value), () => !IsBusy);
        PlanIntegrityCommand = new(PlanIntegrityAsync, () => !IsBusy && _maintenance is not null, SetError);
        ExecuteIntegrityCommand = new(ExecuteIntegrityAsync, () => !IsBusy && _integrityPlan is { IsElevated: true }, SetError);
    }

    public Task<bool> RunSelectedMaintenanceQueryAsync() => RunMaintenanceQueryAsync(SelectedMaintenanceQuery.Query);
    public Task<bool> RunMaintenanceQueryAsync(MaintenanceQuery query)
    {
        if (IsBusy) return Task.FromResult(false);
        var binding = _maintenance?.ResolveQuery(query);
        if (binding is null || !binding.IsSuccess)
        { StatusMessage = binding?.Error?.Message ?? "As consultas não estão configuradas nesta composição."; return Task.FromResult(false); }
        return RunMaintenanceCollectionAsync(binding.Value);
    }
    public Task<bool> RunDefenderStatusAsync() => RunMaintenanceQueryAsync(MaintenanceQuery.Defender);
    public Task<bool> RunDeviceInventoryAsync() => RunMaintenanceQueryAsync(MaintenanceQuery.DeviceDrivers);
    public Task<bool> RunServiceInventoryAsync() => RunMaintenanceQueryAsync(MaintenanceQuery.Services);
    public Task<bool> RunUpdateHistoryAsync() => RunMaintenanceQueryAsync(MaintenanceQuery.Updates);
    public Task<bool> RunProcessInventoryAsync() => RunMaintenanceQueryAsync(MaintenanceQuery.Processes);
    public Task<bool> RunScheduledTasksAsync() => RunMaintenanceQueryAsync(MaintenanceQuery.ScheduledTasks);
    public Task<bool> RunStorageInventoryAsync() => RunMaintenanceQueryAsync(MaintenanceQuery.Storage);
    public Task<bool> RunFolderSpaceAsync()
    {
        if (IsBusy) return Task.FromResult(false);
        if (string.IsNullOrWhiteSpace(FolderSpacePath)) { StatusMessage = "Selecione uma pasta local para analisar o espaço."; return Task.FromResult(false); }
        // Capture the selection once; edits during the await must not relabel the collected source.
        var selectedPath = FolderSpacePath;
        var service = _maintenance?.FolderSpace?.Invoke(selectedPath);
        return RunMaintenanceCollectionAsync(service is null ? null : new(MaintenanceCollectors.FolderSpace, service));
    }

    private async Task<bool> RunMaintenanceCollectionAsync(MaintenanceQueryBinding? binding)
    {
        if (binding is null) { StatusMessage = "Esta consulta não está configurada nesta composição."; return false; }
        bool applied = false;
        await RunModuleAsync(async token =>
        {
            StatusMessage = "Consultando o estado do Windows, sem alterar configurações…";
            var collected = await binding.RunAsync(null, token);
            if (!collected.IsSuccess) { StatusMessage = collected.Error!.Message; return; }
            applied = (await ApplyMaintenanceEvidenceAsync(collected.Value!, binding.CollectorId, token)).Value;
        });
        return applied;
    }

    private async Task<Result<bool>> ApplyMaintenanceEvidenceAsync(DiagnosticSession collected, string collectorId, CancellationToken token)
    {
        var merged = EvidenceSessionUpdates.ReplaceOwned(CurrentSession, collected, new HashSet<string>([collectorId], StringComparer.Ordinal));
        if (!merged.IsSuccess) { StatusMessage = merged.Error!.Message; return Result<bool>.Failure(merged.Error.Code, merged.Error.Message); }
        var saved = await PersistCollectionAsync(merged.Value!, token, "Evidências da manutenção salvas. Confira a cobertura e as datas de cada fonte.");
        return saved.IsSuccess ? Result<bool>.Success(true) : Result<bool>.FailureWithValue(true, saved.Error!.Code, saved.Error.Message);
    }

    public async Task PlanIntegrityAsync()
    {
        if (_maintenance is null) return;
        await RunModuleAsync(async token =>
        {
            var revision = _integrityRevision;
            _integrityPlan = null;
            var result = await _maintenance.Integrity.PlanAsync(SelectedIntegrityChoice.Operation, token);
            if (revision != _integrityRevision) { IntegrityPreview = "A seleção mudou durante a prévia. Prepare outra prévia."; return; }
            if (!result.IsSuccess) { IntegrityPreview = result.Error!.Message; return; }
            _integrityPlan = result.Value!;
            var plan = _integrityPlan;
            IntegrityPreview = $"{plan.DisplayName}\nExecutável: {plan.ExecutablePath}\nArgumentos: {string.Join(" ", plan.Arguments)}\nSHA-256: {plan.ExecutableSha256}\nPrazo: {plan.Timeout}\nContexto elevado: {(plan.IsElevated ? "sim" : "não; execução indisponível neste contexto")}\n{string.Join("\n", plan.Limitations)}\nPlano: {plan.Id}";
            StatusMessage = "Prévia preparada. Nenhuma verificação de integridade foi iniciada.";
        });
    }

    public async Task ExecuteIntegrityAsync()
    {
        var plan = _integrityPlan;
        if (plan is null || IsBusy || _maintenance is null) return;
        if (!plan.IsElevated) { IntegrityResult = "A execução exige contexto já elevado. O aplicativo não solicita elevação automática."; return; }
        if (!_confirmation.Confirm("opTImus · Confirmar verificação", IntegrityPreview + "\n\nConfirma executar esta verificação sem reparo? O Windows pode gravar logs de diagnóstico.")) return;
        if (!ReferenceEquals(plan, _integrityPlan)) { IntegrityPreview = "O plano mudou durante a confirmação. Prepare outra prévia."; return; }
        _integrityPlan = null;
        await RunModuleAsync(async token =>
        {
            IntegrityResult = "Resultado desta verificação ainda não confirmado.";
            StatusMessage = "Verificação em execução. Não são solicitados reparos.";
            var result = await _maintenance.Integrity.ExecuteAsync(plan, plan.Id, token);
            string? storageFailure = null;
            if (result.Value is { } receipt)
            {
                IntegrityResult = $"Estado do comando: {receipt.State}\nReparo executado: {(receipt.RepairPerformed ? "informado pelo adaptador" : "não")}\nRecibo: {receipt.JournalDirectory}\n{receipt.RecoveryInstruction}\n{string.Join("\n", receipt.Limitations)}";
                var evidence = receipt.Evidence;
                var now = DateTimeOffset.UtcNow;
                var collected = new DiagnosticSession(Guid.NewGuid(), now, now,
                    evidence.Status == EvidenceStatus.Complete ? SessionStatus.Complete : SessionStatus.Partial,
                    [evidence], DiagnosticRules.Evaluate([evidence]));
                // The durable operation receipt exists independently of session-store cancellation.
                var applied = await ApplyMaintenanceEvidenceAsync(collected, MaintenanceCollectors.Integrity, CancellationToken.None);
                if (!applied.IsSuccess) storageFailure = applied.Error!.Message;
            }
            if (!result.IsSuccess) { IntegrityResult += "\n" + result.Error!.Message; StatusMessage = "Resultado não confirmado como completo. Consulte o recibo antes de repetir."; }
            else StatusMessage = "Comando encerrado. Examine a saída original; o término não é um laudo de integridade.";
            if (storageFailure is not null) StatusMessage += " O recibo da operação foi preservado; a gravação no histórico de diagnósticos não foi confirmada: " + storageFailure;
            if (token.IsCancellationRequested) StatusMessage += " Cancelamento recebido; os fatos e recibos já obtidos foram preservados.";
        });
    }

    private void RefreshMaintenanceCommands()
    {
        Changed(nameof(IntegrityHasPreparedPlan));
        foreach (var command in new[] { RunMaintenanceQueryCommand, FolderSpaceCommand, PlanIntegrityCommand, ExecuteIntegrityCommand }) command?.Notify();
        BrowseFolderSpaceCommand?.Notify();
    }
}
