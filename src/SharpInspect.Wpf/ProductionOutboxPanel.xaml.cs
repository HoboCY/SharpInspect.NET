using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SharpInspect.Wpf;

public partial class ProductionOutboxPanel : UserControl
{
    public ProductionOutboxPanel()
    {
        InitializeComponent();
        Unloaded += (_, _) => Deactivate();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is ProductionOutboxViewModel old)
            {
                old.SensitiveInputsInvalidated -= ClearSensitiveInputs;
                old.Deactivate();
            }
            if (args.NewValue is ProductionOutboxViewModel current)
                current.SensitiveInputsInvalidated += ClearSensitiveInputs;
            ClearSensitiveInputs(this, EventArgs.Empty);
        };
    }
    public void Deactivate()
    {
        Dispatcher.VerifyAccess();
        (DataContext as ProductionOutboxViewModel)?.Deactivate();
        ClearSensitiveInputs(this, EventArgs.Empty);
    }
    private void ClearSensitiveInputs(object? sender, EventArgs args)
    {
        StepUpPasswordBox.Clear();
        FileStatusText.Text = string.Empty;
    }
    private async void RecoverClick(object sender, RoutedEventArgs args) => await SubmitAsync(false);
    private async void CorrectClick(object sender, RoutedEventArgs args) => await SubmitAsync(true);
    private async Task SubmitAsync(bool correction)
    {
        var password = StepUpPasswordBox.Password;
        StepUpPasswordBox.Clear();
        try
        {
            if (DataContext is not ProductionOutboxViewModel model) return;
            if (correction) await model.CorrectWithStepUpAsync(password);
            else await model.RecoverWithStepUpAsync(password);
        }
        finally { password = string.Empty; StepUpPasswordBox.Clear(); }
    }
    private async void SelectCorrectionClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is not ProductionOutboxViewModel model || !model.CanEditOperations) return;
        var selected = model.SelectedItem;
        var dialog = new OpenFileDialog { Title = "选择最终更正报文", CheckFileExists = true, Multiselect = false,
            Filter = "报文文件|*.json;*.xml;*.bin;*.txt|所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        byte[]? bytes = null;
        try
        {
            await using var stream = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read,
                FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is < 1 or > 8 * 1024 * 1024)
                throw new InvalidOperationException("CorrectionPayloadSizeRejected");
            bytes = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(offset));
                if (count == 0) throw new EndOfStreamException();
                offset += count;
            }
            if (stream.Length != bytes.Length) throw new InvalidOperationException("CorrectionPayloadChanged");
            if (ReferenceEquals(DataContext, model) && ReferenceEquals(model.SelectedItem, selected) && model.CanEditOperations)
            {
                model.SetCorrectionPayload(dialog.SafeFileName, bytes);
                FileStatusText.Text = string.Empty;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (ReferenceEquals(DataContext, model) && model.CanEditOperations)
                FileStatusText.Text = "无法读取所选报文；文件须为 1 字节至 8 MiB，工位策略可能采用更低上限。";
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
}
