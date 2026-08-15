using System.Windows;
using DicomMover.Models;

namespace DicomMover;

public partial class DicomElementDetailsWindow : Window
{
    private readonly DicomElementTechnicalDetails _details;

    public DicomElementDetailsWindow(DicomElementTechnicalDetails details)
    {
        InitializeComponent();
        _details = details;
        TagText.Text = details.Tag;
        NameText.Text = details.Name;
        VrText.Text = details.VrDisplay;
        SourceValueText.Text = details.SourceValue;
        SourceCharsetText.Text = details.SourceCharset;
        ActualEncodingLabel.Text = details.ActualEncodingCaption;
        ActualEncodingText.Text = details.ActualEncodingDisplay;
        DetectionMethodText.Text = details.DetectionMethod;
        PrivateCreatorLabel.Visibility = PrivateCreatorText.Visibility = details.ShowPrivateCreator ? Visibility.Visible : Visibility.Collapsed;
        PrivateCreatorText.Text = details.PrivateCreator ?? "—";
        StatusText.Text = details.Status;
        ReasonLabel.Visibility = ReasonText.Visibility = details.ShowReason ? Visibility.Visible : Visibility.Collapsed;
        ReasonText.Text = details.Reason;
        RawHexText.Text = details.RawHex;

        if (!details.HasConversion) return;
        ConversionPanel.Visibility = Visibility.Visible;
        TargetEncodingText.Text = details.TargetEncoding;
        SavedCharsetText.Text = details.SavedCharset;
        TransformedValueText.Text = details.ValueAfterTransformation;
        ReloadedValueText.Text = details.ValueAfterReload;
        ChangedText.Text = details.TextMatches;
        VerificationText.Text = details.ElementVerificationResult;
        FileVerificationText.Text = details.FileVerificationResult;
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(_details.CopyText); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Копирование", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
