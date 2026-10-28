using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Optimus.Infrastructure;
using Optimus.Application;

namespace Optimus.Desktop;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string? dataDirectory = null;
            string? previewPath = null;
            string? profilePath = null;
            int? previewTab = null;
            WorkspacePage? previewPage = null;
            bool emptyPreview = false;
            bool analyzePreview = false;
            bool inventoryPreview = false;
            bool maintenancePreview = false;
            bool maintenanceToolsPreview = false;
            bool systemToolsPreview = false;
            bool supportToolsPreview = false;
            bool recoveryToolsPreview = false;
            bool instabilityPreview = false;
            string? memoryReportPreview = null;
            string? dumpPreview = null;
            string? folderSpacePreview = null;
            string? integrityPreview = null;
            for (int index = 0; index < e.Args.Length; index += 2)
            {
                if (index + 1 >= e.Args.Length) throw new ArgumentException("Argumentos inválidos.");
                switch (e.Args[index])
                {
                    case "--data-dir" when dataDirectory is null: dataDirectory = e.Args[index + 1]; break;
                    case "--render-preview" when previewPath is null: previewPath = e.Args[index + 1]; break;
                    case "--profile" when profilePath is null: profilePath = e.Args[index + 1]; break;
                    case "--preview-tab" when previewTab is null && int.TryParse(e.Args[index + 1], out var tab) && tab is >= 0 and <= 7: previewTab = tab; break;
                    case "--preview-page" when previewPage is null && Enum.TryParse<WorkspacePage>(e.Args[index + 1], true, out var page) && Enum.IsDefined(page): previewPage = page; break;
                    case "--preview-empty" when bool.TryParse(e.Args[index + 1], out var empty): emptyPreview = empty; break;
                    case "--analyze-preview" when bool.TryParse(e.Args[index + 1], out var analyze): analyzePreview = analyze; break;
                    case "--inventory-preview" when bool.TryParse(e.Args[index + 1], out var inventory): inventoryPreview = inventory; break;
                    case "--maintenance-preview" when bool.TryParse(e.Args[index + 1], out var maintenance): maintenancePreview = maintenance; break;
                    case "--maintenance-tools-preview" when bool.TryParse(e.Args[index + 1], out var maintenanceTools): maintenanceToolsPreview = maintenanceTools; break;
                    case "--system-tools-preview" when bool.TryParse(e.Args[index + 1], out var systemTools): systemToolsPreview = systemTools; break;
                    case "--support-tools-preview" when bool.TryParse(e.Args[index + 1], out var supportTools): supportToolsPreview = supportTools; break;
                    case "--recovery-tools-preview" when bool.TryParse(e.Args[index + 1], out var recoveryTools): recoveryToolsPreview = recoveryTools; break;
                    case "--instability-preview" when bool.TryParse(e.Args[index + 1], out var instability): instabilityPreview = instability; break;
                    case "--memory-report-preview" when memoryReportPreview is null: memoryReportPreview = e.Args[index + 1]; break;
                    case "--dump-preview" when dumpPreview is null: dumpPreview = e.Args[index + 1]; break;
                    case "--folder-space-preview" when folderSpacePreview is null: folderSpacePreview = e.Args[index + 1]; break;
                    case "--integrity-preview" when integrityPreview is null: integrityPreview = e.Args[index + 1]; break;
                    default: throw new ArgumentException("Argumento não reconhecido.");
                }
            }

            if ((previewPage is not null || emptyPreview || inventoryPreview || maintenancePreview || maintenanceToolsPreview || systemToolsPreview || supportToolsPreview || recoveryToolsPreview || instabilityPreview || memoryReportPreview is not null || dumpPreview is not null || folderSpacePreview is not null || integrityPreview is not null) && previewPath is null)
                throw new ArgumentException("As consultas de verificação visual requerem --render-preview.");
            if (previewPage is not null && previewTab is not null)
                throw new ArgumentException("Escolha --preview-page ou --preview-tab, sem combinar os seletores.");
            if (emptyPreview && (analyzePreview || inventoryPreview || maintenancePreview || maintenanceToolsPreview || systemToolsPreview || supportToolsPreview || recoveryToolsPreview || instabilityPreview || memoryReportPreview is not null || dumpPreview is not null || folderSpacePreview is not null || integrityPreview is not null))
                throw new ArgumentException("A prévia vazia não pode solicitar consultas ou análises.");
            var viewModel = new MainViewModel(Composition.CreateDiagnostics(), Composition.CreateStore(dataDirectory), new ReportExporter(), Composition.CreateModules(dataDirectory),
                inventory: Composition.CreateInventory(), maintenance: Composition.CreateMaintenance(dataDirectory), instability: Composition.CreateInstability(dataDirectory));
            var window = new MainWindow { DataContext = viewModel };
            MainWindow = window;
            if (previewPath is not null)
            {
                window.ShowInTaskbar = false;
                window.ShowActivated = false;
                window.Left = -20_000;
                window.Top = -20_000;
            }
            window.IsEnabled = false;
            window.Show();
            await viewModel.InitializeAsync();
            if (profilePath is not null) await viewModel.LoadProfileAsync(profilePath);
            window.IsEnabled = true;
            if (previewPath is null) return;
            if (!emptyPreview) await viewModel.RunDiagnosticAsync();
            bool inventoryApplied = !inventoryPreview || await viewModel.RunInventoryAsync();
            bool maintenanceApplied = true, integrityPrepared = true;
            if (maintenancePreview)
            {
                maintenanceApplied = await viewModel.RunDefenderStatusAsync();
                maintenanceApplied = await viewModel.RunDeviceInventoryAsync() && maintenanceApplied;
            }
            if (maintenanceToolsPreview)
            {
                maintenanceApplied = await viewModel.RunServiceInventoryAsync() && maintenanceApplied;
                maintenanceApplied = await viewModel.RunUpdateHistoryAsync() && maintenanceApplied;
            }
            if (folderSpacePreview is not null)
            {
                viewModel.FolderSpacePath = folderSpacePreview;
                maintenanceApplied = await viewModel.RunFolderSpaceAsync() && maintenanceApplied;
            }
            if (systemToolsPreview)
            {
                maintenanceApplied = await viewModel.RunProcessInventoryAsync() && maintenanceApplied;
                maintenanceApplied = await viewModel.RunScheduledTasksAsync() && maintenanceApplied;
                maintenanceApplied = await viewModel.RunStorageInventoryAsync() && maintenanceApplied;
            }
            if (supportToolsPreview)
            {
                foreach (var query in new[] { MaintenanceQuery.Connections, MaintenanceQuery.Firewall, MaintenanceQuery.ApplicationFailures })
                {
                    viewModel.SelectedMaintenanceQuery = viewModel.MaintenanceQueryChoices.Single(choice => choice.Query == query);
                    maintenanceApplied = await viewModel.RunSelectedMaintenanceQueryAsync() && maintenanceApplied;
                }
            }
            if (recoveryToolsPreview)
            {
                foreach (var query in new[] { MaintenanceQuery.RestorePoints, MaintenanceQuery.BitLocker, MaintenanceQuery.PendingRestart })
                {
                    viewModel.SelectedMaintenanceQuery = viewModel.MaintenanceQueryChoices.Single(choice => choice.Query == query);
                    maintenanceApplied = await viewModel.RunSelectedMaintenanceQueryAsync() && maintenanceApplied;
                }
            }
            if (integrityPreview is not null)
            {
                if (!MaintenanceOperations.TryParseIntegrity(integrityPreview, out var operation)) throw new ArgumentException("Verificação não reconhecida.");
                viewModel.SelectedIntegrityChoice = viewModel.IntegrityChoices.Single(item => item.Operation == operation);
                await viewModel.PlanIntegrityAsync();
                integrityPrepared = viewModel.IntegrityHasPreparedPlan;
            }
            if (memoryReportPreview is not null)
                maintenanceApplied = await viewModel.ImportMemoryReportFromPathAsync(memoryReportPreview) && maintenanceApplied;
            if (dumpPreview is not null)
            {
                viewModel.DumpPath = dumpPreview;
                maintenanceApplied = await viewModel.RunDumpAnalysisAsync() && maintenanceApplied;
            }
            if (instabilityPreview)
            {
                viewModel.UseInvestigationAi = false;
                maintenanceApplied = await viewModel.RunInvestigationAsync() && maintenanceApplied;
            }
            if (analyzePreview && inventoryApplied && maintenanceApplied) await viewModel.AnalyzeCurrentSessionAsync();
            // Preserve the historical --preview-tab contract while navigation uses named pages.
            if (previewTab is { } selectedTab) viewModel.SelectedPage = selectedTab switch
            {
                0 => WorkspacePage.Diagnostics,
                1 => WorkspacePage.History,
                2 => WorkspacePage.Assistant,
                3 => WorkspacePage.Backup,
                4 => WorkspacePage.Settings,
                5 => WorkspacePage.Inventory,
                6 => WorkspacePage.Maintenance,
                _ => WorkspacePage.Instability
            };
            if (previewPage is { } selectedPage) viewModel.SelectedPage = selectedPage;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var visual = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = new FileStream(Path.GetFullPath(previewPath), FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(output);
            window.Close();
            Shutdown((!emptyPreview && viewModel.CurrentSession is null) || !inventoryApplied || !maintenanceApplied || !integrityPrepared
                || (analyzePreview && !viewModel.AssistantHasValidatedAnswer) ? 1 : 0);
        }
        catch (Exception error)
        {
            if (e.Args.Contains("--render-preview")) Console.Error.WriteLine(error);
            else MessageBox.Show(error.Message, "opTImus · Não foi possível iniciar", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
