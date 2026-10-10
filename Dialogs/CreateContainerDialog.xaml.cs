using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WSLCManager.Helpers;
using WSLCManager.Models;

namespace WSLCManager.Dialogs;

public partial class CreateContainerDialog : Window
{
    private readonly ObservableCollection<ContainerSpec.PortMap> _ports = new();
    private readonly ObservableCollection<ContainerSpec.VolumeMap> _volumes = new();
    private readonly ObservableCollection<ContainerSpec.EnvVar> _envs = new();
    private readonly DispatcherTimer _previewTimer = new();
    private readonly IEnumerable<ImageInfo> _localImages;

    private bool _ready;

    public ContainerSpec Result { get; private set; }

    public CreateContainerDialog(IEnumerable<ImageInfo> localImages)
    {
        InitializeComponent();

        _localImages = localImages ?? Enumerable.Empty<ImageInfo>();

        ListPorts.ItemsSource = _ports;
        ListVolumes.ItemsSource = _volumes;
        ListEnv.ItemsSource = _envs;

        ListLocalImages.ItemsSource = _localImages
            .Select(i => i.Reference)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .Take(24)
            .ToList();

        _previewTimer.Interval = TimeSpan.FromMilliseconds(400);
        _previewTimer.Tick += (s, e) => UpdatePreview();
        _previewTimer.Start();

        Loaded += (s, e) =>
        {
            _ready = true;
            ChromeHelper.CenterOnOwner(this);
            // 默认给一行端口，降低上手成本
            if (_ports.Count == 0) _ports.Add(new ContainerSpec.PortMap());
            UpdatePreview();
        };

        Closed += (s, e) => _previewTimer.Stop();
    }

    /// <summary>由「用此镜像新建容器」调用（ShowDialog 之前）。</summary>
    public void PresetImage(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return;
        TxtImage.Text = reference;
        UpdatePreview();
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // ── 行增删 ──
    private void OnAddPort(object sender, RoutedEventArgs e)
    {
        _ports.Add(new ContainerSpec.PortMap());
        UpdatePreview();
    }

    private void OnRemovePort(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ContainerSpec.PortMap p) _ports.Remove(p);
        UpdatePreview();
    }

    private void OnAddVolume(object sender, RoutedEventArgs e)
    {
        _volumes.Add(new ContainerSpec.VolumeMap());
        UpdatePreview();
    }

    private void OnRemoveVolume(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ContainerSpec.VolumeMap v) _volumes.Remove(v);
        UpdatePreview();
    }

    private void OnAddEnv(object sender, RoutedEventArgs e)
    {
        _envs.Add(new ContainerSpec.EnvVar());
        UpdatePreview();
    }

    private void OnRemoveEnv(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ContainerSpec.EnvVar x) _envs.Remove(x);
        UpdatePreview();
    }

    private void OnPickLocalImage(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Content is string s) TxtImage.Text = s;
    }

    private void OnAnyChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        try
        {
            if (TxtPreview == null) return;
            TxtPreview.Text = BuildSpec().PreviewCommandLine();
        }
        catch { }
    }

    private ContainerSpec BuildSpec()
    {
        var spec = new ContainerSpec
        {
            Image = TxtImage.Text?.Trim() ?? "",
            Name = TxtName.Text?.Trim() ?? "",
            Command = TxtCommand.Text?.Trim() ?? "",
            Detach = ChkDetach.IsChecked == true,
            AutoRemove = ChkAutoRemove.IsChecked == true,
            Tty = ChkTty.IsChecked == true,
            Interactive = ChkTty.IsChecked == true,
            RestartPolicy = TxtRestart.Text?.Trim() ?? "",
            WorkingDir = TxtWorkDir.Text?.Trim() ?? ""
        };

        foreach (var p in _ports)
        {
            if (string.IsNullOrWhiteSpace(p.ContainerPort)) continue;
            spec.Ports.Add(new ContainerSpec.PortMap
            {
                HostPort = (p.HostPort ?? "").Trim(),
                ContainerPort = (p.ContainerPort ?? "").Trim(),
                Protocol = (p.Protocol ?? "").Trim()
            });
        }
        foreach (var v in _volumes)
        {
            if (string.IsNullOrWhiteSpace(v.HostPath) && string.IsNullOrWhiteSpace(v.ContainerPath)) continue;
            spec.Volumes.Add(new ContainerSpec.VolumeMap
            {
                HostPath = (v.HostPath ?? "").Trim(),
                ContainerPath = (v.ContainerPath ?? "").Trim(),
                ReadOnly = v.ReadOnly
            });
        }
        foreach (var x in _envs)
        {
            if (string.IsNullOrWhiteSpace(x.Key)) continue;
            spec.Env.Add(new ContainerSpec.EnvVar { Key = x.Key.Trim(), Value = (x.Value ?? "").Trim() });
        }
        return spec;
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var spec = BuildSpec();
        if (string.IsNullOrWhiteSpace(spec.Image))
        {
            AppDialog.Info(this, "缺少镜像", "请先填写镜像名称，例如 alpine:latest。");
            TxtImage.Focus();
            return;
        }
        Result = spec;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
