using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using AvaloniaEdit.TextMate.Grammars;
using MessagePack;
using mqttMultimeter.Extensions;
using mqttMultimeter.Main;
using mqttMultimeter.Services.Data;
using mqttMultimeter.Text;

namespace mqttMultimeter.Controls;

public sealed class BufferInspectorView : TemplatedControl
{
    static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        WriteIndented = true
    };

    public static readonly StyledProperty<byte[]?> BufferProperty = AvaloniaProperty.Register<BufferInspectorView, byte[]?>(nameof(Buffer));

    public static readonly StyledProperty<BufferConverter?> SelectedFormatProperty = AvaloniaProperty.Register<BufferInspectorView, BufferConverter?>(nameof(SelectedFormat));

    public static readonly StyledProperty<IList<BufferConverter>> FormatsProperty = AvaloniaProperty.Register<BufferInspectorView, IList<BufferConverter>>(nameof(Formats));

    public static readonly StyledProperty<bool> ShowRawProperty = AvaloniaProperty.Register<BufferInspectorView, bool>(nameof(ShowRaw));

    public static readonly StyledProperty<string?> SelectedFormatNameProperty = AvaloniaProperty.Register<BufferInspectorView, string?>(nameof(SelectedFormatName), "UTF-8");

    readonly RegistryOptions _textEditorRegistryOptions = new(ThemeName.Dark);

    string _content = string.Empty;
    Button? _copyToClipboardButton;
    string? _currentTextEditorLanguage;
    Button? _saveToFileButton;
    TextEditor? _textEditor;
    TextMate.Installation? _textMateInstallation;

    public BufferInspectorView()
    {
        Formats = SharedConverters;

        SelectFormat();
    }

    public byte[]? Buffer
    {
        get => GetValue(BufferProperty);
        set => SetValue(BufferProperty, value);
    }

    public IList<BufferConverter> Formats
    {
        get => GetValue(FormatsProperty);
        set => SetValue(FormatsProperty, value);
    }

    public BufferConverter? SelectedFormat
    {
        get => GetValue(SelectedFormatProperty);
        set => SetValue(SelectedFormatProperty, value);
    }

    public string? SelectedFormatName
    {
        get => GetValue(SelectedFormatNameProperty);
        set => SetValue(SelectedFormatNameProperty, value);
    }

    static ObservableCollection<BufferConverter> SharedConverters { get; } = new()
    {
        new BufferConverter("ASCII", null, b => Encoding.ASCII.GetString(b)),
        new BufferConverter("Base64", null, Convert.ToBase64String),
        new BufferConverter("Binary", null, BinaryEncoder.GetString),
        new BufferConverter("HEX", null, HexEncoder.GetString),
        new BufferConverter("JSON",
            "source.json.comments",
            b =>
            {
                var json = Encoding.UTF8.GetString(b);
                return JsonSerializer.Serialize(JsonNode.Parse(json), JsonSerializerOptions);
            }),

        new BufferConverter("MessagePack as JSON",
            "source.json.comments",
            b =>
            {
                var json = MessagePackSerializer.ConvertToJson(b);
                return JsonSerializerService.Instance?.Format(json) ?? string.Empty;
            }),

        new BufferConverter("RAW", null, _ => "RAW"), // Special case!
        new BufferConverter("Unicode", null, b => Encoding.Unicode.GetString(b)),
        new BufferConverter("UTF-8", null, b => Encoding.UTF8.GetString(b)),
        new BufferConverter("UTF-32", null, b => Encoding.UTF32.GetString(b)),
        new BufferConverter("XML",
            "text.xml",
            b =>
            {
                var xml = Encoding.UTF8.GetString(b);
                return XDocument.Parse(xml).ToString(SaveOptions.None);
            })
    };

    public bool ShowRaw
    {
        get => GetValue(ShowRawProperty);
        set => SetValue(ShowRawProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _textEditor = (TextEditor)this.GetTemplateChild("TextEditor");
        _textMateInstallation = _textEditor.InstallTextMate(_textEditorRegistryOptions);
        SyncTextEditor();

        _copyToClipboardButton = (Button)this.GetTemplateChild("CopyToClipboardButton");
        _copyToClipboardButton.Click += OnCopyToClipboard;

        _saveToFileButton = (Button)this.GetTemplateChild("SaveToFileButton");
        _saveToFileButton.Click += OnSaveToFile;
        ReadBuffer();
    }

    protected override void OnPropertyChanged<T>(AvaloniaPropertyChangedEventArgs<T> change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BufferProperty || change.Property == SelectedFormatProperty)
        {
            ReadBuffer();
        }

        if (change.Property == SelectedFormatProperty)
        {
            ShowRaw = SelectedFormat?.Name == "RAW";
        }

        if (change.Property == SelectedFormatNameProperty)
        {
            SelectFormat();
        }
    }

    void OnCopyToClipboard(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_content))
        {
            Application.Current?.Clipboard?.SetTextAsync(_content);
        }
    }

    void OnSaveToFile(object? sender, RoutedEventArgs e)
    {
        var saveFileDialog = new SaveFileDialog();
        saveFileDialog.Filters!.Add(new FileDialogFilter
        {
            Name = "Binary files",
            Extensions = new List<string>
            {
                "bin"
            }
        });

        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var fileName = await saveFileDialog.ShowAsync(MainWindow.Instance);
            if (!string.IsNullOrEmpty(fileName))
            {
                await File.WriteAllBytesAsync(fileName, Buffer ?? Array.Empty<byte>(), CancellationToken.None);
            }
        });
    }

    void ReadBuffer()
    {
        var format = SelectedFormat;
        if (format == null)
        {
            throw new InvalidOperationException();
        }

        if ((Buffer?.Length ?? 0) == 0)
        {
            _content = string.Empty;
        }
        else
        {
            try
            {
                _content = format.Convert(Buffer!);
            }
            catch (Exception exception)
            {
                _content = $"<{exception.Message}>";
            }
        }

        SyncTextEditor();
    }

    void SelectFormat()
    {
        if (string.IsNullOrEmpty(SelectedFormatName))
        {
            SelectedFormat = Formats.FirstOrDefault();
        }

        SelectedFormat = Formats.FirstOrDefault(f => string.Equals(f.Name, SelectedFormatName));

        if (SelectedFormat == null)
        {
            SelectedFormat = Formats.FirstOrDefault();
        }
    }

    void SyncTextEditor()
    {
        if (_textEditor == null)
        {
            return;
        }

        _textEditor.Text = _content;

        if (SelectedFormat == null)
        {
            return;
        }

        if (_textMateInstallation == null)
        {
            return;
        }

        // Avoid updating the language all the time even without a change!
        if (string.Equals(_currentTextEditorLanguage, SelectedFormat.Grammar))
        {
            return;
        }

        _currentTextEditorLanguage = SelectedFormat.Grammar;

        if (SelectedFormat.Grammar == null)
        {
            _textMateInstallation.SetGrammar(_currentTextEditorLanguage);
        }
        else
        {
            _textMateInstallation.SetGrammar(_textEditorRegistryOptions.GetScopeByLanguageId(_currentTextEditorLanguage));
        }
    }
}