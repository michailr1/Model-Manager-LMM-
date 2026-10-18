using System.Windows;
using Microsoft.Win32;
using LocalModelManager.Core;
using LocalModelManager.Infrastructure.LMStudio;

namespace LocalModelManager.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    public UpdateViewModel Updates { get; }
    public MainWindow()
    {

        model = new MainViewModel(ConfirmImport, ConfirmLoad);
        Updates = new UpdateViewModel(model);
        InitializeComponent();
        DataContext = model;
        Loaded += async (_, _) => { model.DiscoverCommand.Execute(null); await Updates.StartAsync(); };
        Closed += (_, _) => { Updates.Dispose(); model.Cancel(); };
    }
    private bool ConfirmImport(ImportRequest request, string preview)
    {
        var mode = request.Mode == ImportMode.Copy
            ? $"COPY: будет создана полная копия ({request.File.SizeBytes:N0} bytes / {request.File.SizeBytes / 1073741824.0:F2} GiB). Требуется свободное место на диске назначения."
            : "SYMBOLIC LINK: исходный файл останется на месте. Не перемещайте и не удаляйте его после подключения.";
        return MessageBox.Show(this,
            $"{mode}\n\nИсточник: {request.File.FullPath}\nКатегория: {request.Repository}\n\nПлан LM Studio:\n{preview}\n\nВыполнить импорт?",
            request.Mode == ImportMode.Copy ? "Подтвердите копирование модели" : "Подключение модели",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
    private bool ConfirmLoad(LoadPlan plan) => MessageBox.Show(this,
        $"Загрузить модель в память?\n\nМодель: {plan.ModelKey}\nКонтекст Hermes: 65 536 токенов\nДоля GPU: {plan.Estimate.OffloadPercent}%\nОценка VRAM: {plan.Estimate.GpuGiB:F2} GiB при бюджете {plan.GpuBudgetGiB:F2} GiB\nПолная оценка памяти LMS: {plan.Estimate.TotalGiB:F2} GiB\nСвободно RAM: {plan.FreeRamGiB:F2} GiB; резерв RAM: {plan.RamReserveGiB:F2} GiB\n\nЭто реальная загрузка. Консервативная проверка RAM требует полную оценку LMS плюс резерв. Проверки не гарантируют успешную загрузку.\n\nНачать загрузку?",
        "Подтвердите загрузку модели", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    private void OnExportHermes(object sender, RoutedEventArgs e)
    {
        var useLan = sender is System.Windows.Controls.Button { Tag: "lan" };
        try
        {
            _ = model.ProfileForExport(useLan);
            var dialog = new SaveFileDialog
            {
                Filter = "Hermes YAML profile|*.yaml", AddExtension = true, DefaultExt = ".yaml",
                FileName = useLan ? "hermes-lmm-lan.yaml" : "hermes-lmm-local.yaml", OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;
            System.IO.File.WriteAllText(dialog.FileName, model.ProfileForExport(useLan));
            model.ReportProfileExport(dialog.FileName);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Профиль Hermes", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "GGUF models|*.gguf", Multiselect = false };
        if (dialog.ShowDialog() == true) model.SelectFiles([dialog.FileName]);
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = model.IsIdle && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files &&
            string.Equals(System.IO.Path.GetExtension(files[0]), ".gguf", StringComparison.OrdinalIgnoreCase)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void OnDrop(object sender, DragEventArgs e)
    {
        model.SelectFiles(e.Data.GetData(DataFormats.FileDrop) as string[] ?? []);
        e.Handled = true;
    }
}
