using System.Globalization;
using System.Windows.Input;

namespace Optimus.Desktop;

public enum WorkspacePage { Home, Diagnostics, Instability, Maintenance, Inventory, Backup, Assistant, History, Settings }

public sealed partial class MainViewModel
{
    private WorkspacePage _selectedPage = WorkspacePage.Home;
    private string _maintenanceSearch = "";

    public WorkspacePage SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (!Enum.IsDefined(value) || value == _selectedPage) return;
            _selectedPage = value;
            Changed(); Changed(nameof(PageTitle)); Changed(nameof(PageDescription));
        }
    }

    public string PageTitle => SelectedPage switch
    {
        WorkspacePage.Home => "Como podemos ajudar?",
        WorkspacePage.Diagnostics => "Diagnóstico do computador",
        WorkspacePage.Instability => "Investigar instabilidade",
        WorkspacePage.Maintenance => "Consultas e verificações",
        WorkspacePage.Inventory => "Inventário do Windows",
        WorkspacePage.Backup => "Backup e recuperação",
        WorkspacePage.Assistant => "Assistente com IA local",
        WorkspacePage.History => "Histórico de diagnósticos",
        _ => "Configuração de ferramentas"
    };

    public string PageDescription => SelectedPage switch
    {
        WorkspacePage.Home => "Escolha uma tarefa. O diagnóstico básico já está disponível.",
        WorkspacePage.Diagnostics => "Consulte os sinais do sistema e entenda os próximos passos.",
        WorkspacePage.Instability => "Reúna registros de reinícios, memória e hardware em uma investigação.",
        WorkspacePage.Maintenance => "Encontre uma consulta específica ou prepare uma verificação do Windows.",
        WorkspacePage.Inventory => "Veja rede, energia, programas e entradas de inicialização.",
        WorkspacePage.Backup => "Prepare o backup, revise a prévia e confirme a execução.",
        WorkspacePage.Assistant => "Faça uma pergunta sobre os dados já coletados.",
        WorkspacePage.History => "Abra uma sessão salva para consultar ou exportar seus dados.",
        _ => "Configure apenas os recursos opcionais que você deseja usar."
    };

    public ICommand NavigateCommand { get; private set; } = null!;
    public AsyncCommand StartDiagnosticCommand { get; private set; } = null!;
    public bool HasSession => CurrentSession is not null;
    public bool HasAiPaths => !string.IsNullOrWhiteSpace(LlamaPath) && !string.IsNullOrWhiteSpace(ModelPath);
    public bool HasResticPath => !string.IsNullOrWhiteSpace(ResticPath);
    public string AiConfigurationStatus => HasAiPaths ? "Caminhos informados" : "Falta configurar motor e modelo";
    public string BackupConfigurationStatus => HasResticPath ? "Caminho informado" : "Falta configurar o restic";
    public string SmartConfigurationStatus => !string.IsNullOrWhiteSpace(SmartPath) ? "Caminho informado" : "Opcional · falta configurar o smartctl";
    public string DumpConfigurationStatus => !string.IsNullOrWhiteSpace(CdbPath) && !string.IsNullOrWhiteSpace(SymbolDirectory)
        ? "Caminhos informados" : "Opcional · falta configurar CDB e símbolos";
    public string AssistantRequirement => IsBusy ? "Aguarde a operação em andamento ou solicite o cancelamento."
        : !HasAiPaths ? "Para usar a IA, informe o motor llama.cpp e o modelo GGUF em Configuração."
        : !HasSession ? "Os caminhos estão informados. Execute um diagnóstico ou abra uma sessão do histórico para ter dados para analisar."
        : "Dados e caminhos informados. Os arquivos e a compatibilidade serão validados ao iniciar a análise.";
    public string BackupRequirement => IsBusy ? "Aguarde a operação em andamento."
        : !HasResticPath ? "Comece configurando o executável restic em Configuração."
        : string.IsNullOrWhiteSpace(BackupSource) || string.IsNullOrWhiteSpace(BackupRepository) || string.IsNullOrWhiteSpace(PasswordFile)
            ? "Informe a pasta de origem, o repositório novo e o arquivo de senha para preparar a prévia."
            : "Revise a prévia antes de confirmar. Os caminhos e permissões serão validados pelo serviço de backup.";
    public string BackupConfirmationHint => _backupPlan is null ? "Prepare uma prévia válida para habilitar a confirmação do backup." : "Prévia preparada. Confira os detalhes antes de confirmar.";
    public string RestoreConfirmationHint => _restorePlan is null ? "Informe o snapshot e o destino novo; prepare a prévia para habilitar a restauração." : "Prévia preparada. A restauração exige confirmação separada.";
    public string SmartRequirement => string.IsNullOrWhiteSpace(SmartPath) ? "Configure o smartctl ou importe um JSON de uma consulta anterior."
        : string.IsNullOrWhiteSpace(SmartTarget) ? "Informe o identificador exato do dispositivo para habilitar a consulta."
        : "Caminho e dispositivo informados. A consulta não inicia autotestes.";
    public string DumpRequirement => DumpInputsSelected ? "Entradas informadas. Arquivos, símbolos e permissões serão validados ao iniciar."
        : "Selecione cdb.exe, uma pasta de símbolos locais e o arquivo .dmp para habilitar a análise.";
    public string InvestigationRequirement => IsBusy ? "Aguarde a operação em andamento ou solicite o cancelamento."
        : string.IsNullOrWhiteSpace(InvestigationQuestion) ? "Descreva o problema para iniciar."
        : InvestigationQuestion.Length > 400 ? "Use uma descrição de até 400 caracteres."
        : InvestigationDays is < 1 or > 365 ? "Escolha um período entre 1 e 365 dias."
        : UseInvestigationAi && !HasAiPaths ? "A IA está marcada, mas faltam os caminhos em Configuração. Desmarque a IA para investigar agora."
        : IncludeDump && !DumpInputsSelected ? "O dump está marcado. Complete os três caminhos nas opções avançadas ou desmarque essa opção."
        : "Pronto para consultar os registros. Não é necessário configurar a IA para esta investigação.";
    public string IntegrityRequirement => _integrityPlan is null ? "Prepare uma prévia para conferir o comando e a permissão necessária."
        : !_integrityPlan.IsElevated ? "Execução indisponível nesta janela: a verificação exige abrir o opTImus como administrador. O aplicativo não solicita elevação automática."
        : "Prévia preparada em contexto de administrador. Revise os detalhes antes de confirmar.";
    public string FirmwareRequirement => SelectedFirmwareReference is null
        ? "Investigue o hardware primeiro. O fabricante precisa ser reconhecido antes de consultar uma página oficial."
        : string.IsNullOrWhiteSpace(FirmwareNotesUrl) ? "Informe a URL oficial das notas de firmware para o fabricante selecionado."
        : "A consulta usa a internet. URL, fabricante e limites serão conferidos antes da leitura.";
    public string DiagnosticOverview => CurrentSession is null ? "Nenhuma coleta aberta"
        : $"{CurrentSession.Evidence.Count} fontes · {CurrentSession.Findings.Count} {(CurrentSession.Findings.Count == 1 ? "achado" : "achados")} de triagem";
    public string DiagnosticNextStep => CurrentSession is null ? "Comece com uma coleta do sistema, memória, volumes e eventos."
        : CurrentSession.Status == Optimus.Domain.SessionStatus.Partial ? "A coleta tem limitações. Confira as fontes parciais ou indisponíveis antes de interpretar os achados."
        : CurrentSession.Findings.Count == 0 ? "Nenhum achado pelas regras desta coleta. Isso não comprova a saúde completa do computador."
        : "Leia os achados e suas recomendações; abra os dados de cada fonte quando precisar de detalhes.";

    public string MaintenanceSearch
    {
        get => _maintenanceSearch;
        set
        {
            if (_maintenanceSearch == value) return;
            _maintenanceSearch = value ?? "";
            Changed(); Changed(nameof(FilteredMaintenanceQueries)); Changed(nameof(HasMaintenanceMatches));
            var visible = FilteredMaintenanceQueries;
            if (visible.Count > 0 && !visible.Contains(SelectedMaintenanceQuery)) SelectedMaintenanceQuery = visible[0];
            Changed(nameof(HasVisibleMaintenanceSelection));
            RunMaintenanceQueryCommand?.Notify();
        }
    }
    public IReadOnlyList<MaintenanceQueryChoice> FilteredMaintenanceQueries => string.IsNullOrWhiteSpace(MaintenanceSearch)
        ? MaintenanceQueryChoices
        : MaintenanceQueryChoices.Where(choice => CultureInfo.GetCultureInfo("pt-BR").CompareInfo.IndexOf(
            choice.Label + " " + choice.Description, MaintenanceSearch.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0).ToArray();
    public bool HasMaintenanceMatches => FilteredMaintenanceQueries.Count > 0;
    public bool HasVisibleMaintenanceSelection => FilteredMaintenanceQueries.Contains(SelectedMaintenanceQuery);

    private void InitializeWorkspace()
    {
        NavigateCommand = new WorkspaceNavigationCommand(page => SelectedPage = page);
        StartDiagnosticCommand = new(async () => { SelectedPage = WorkspacePage.Diagnostics; await RunDiagnosticAsync(); }, () => !IsBusy, SetError);
        PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(IsBusy) or nameof(CurrentSession) or nameof(LlamaPath) or nameof(ModelPath)
                or nameof(ResticPath) or nameof(SmartPath) or nameof(CdbPath) or nameof(SymbolDirectory) or nameof(DumpPath)
                or nameof(BackupSource) or nameof(BackupRepository) or nameof(PasswordFile) or nameof(SmartTarget)
                or nameof(UseInvestigationAi) or nameof(IncludeDump) or nameof(InvestigationQuestion) or nameof(InvestigationDays)
                or nameof(SelectedFirmwareReference) or nameof(FirmwareNotesUrl)) RefreshWorkspaceState();
        };
    }
    private void RefreshWorkspaceState()
    {
        foreach (var name in new[] { nameof(HasSession), nameof(HasAiPaths), nameof(HasResticPath), nameof(AiConfigurationStatus), nameof(BackupConfigurationStatus),
            nameof(SmartConfigurationStatus), nameof(DumpConfigurationStatus), nameof(AssistantRequirement), nameof(BackupRequirement),
            nameof(BackupConfirmationHint), nameof(RestoreConfirmationHint), nameof(SmartRequirement), nameof(DumpRequirement),
            nameof(InvestigationRequirement), nameof(IntegrityRequirement), nameof(FirmwareRequirement), nameof(DiagnosticOverview), nameof(DiagnosticNextStep) }) Changed(name);
        StartDiagnosticCommand?.Notify();
    }
}

internal sealed class WorkspaceNavigationCommand(Action<WorkspacePage> navigate) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => parameter is WorkspacePage page && Enum.IsDefined(page);
    public void Execute(object? parameter) { if (parameter is WorkspacePage page && CanExecute(page)) navigate(page); }
}
