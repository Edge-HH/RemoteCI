using Avalonia.Controls;
using ClassIsland.Shared.Models.Management;
using RemoteCI.Plugin.Services;
using RemoteCI.Plugin.Settings;
using RemoteCI.Plugin.Views.SettingsPages;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

/// <summary>班级策略：本地缓存、消息解析、与内置集控策略的合并，以及设置页锁定。</summary>
public sealed class ClassPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remoteci-policy-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void Store_PersistsAndReloadsPolicy()
    {
        var path = Path.Combine(_root, "ClassPolicy.json");
        var store = new ClassPolicyStore(path);
        // 从未收到过策略：不写入默认值，也就不去改动宿主已有策略。
        Assert.Null(store.Current);

        store.Apply(new ClassPolicy { DisableProfileEditing = true, AllowPluginSettings = false });

        var reloaded = new ClassPolicyStore(path);
        Assert.True(reloaded.Current!.DisableProfileEditing);
        Assert.False(reloaded.Current.AllowPluginSettings);
    }

    [Fact]
    public void Message_ReadsClassPolicyEnvelope()
    {
        var envelope = Envelope.ClassPolicy(new ClassPolicy { DisableDebugMenu = true });
        Assert.True(ClassPolicyMessage.TryRead(envelope, out var parsed));
        Assert.True(parsed.DisableDebugMenu);

        Assert.False(ClassPolicyMessage.TryRead(Envelope.SettingsSync(new { }), out _));
    }

    [Fact]
    public void ApplyTo_WithoutBuiltinManagement_UsesExactValuesAndCanUnlock()
    {
        var target = new ManagementPolicy { DisableProfileEditing = true, AllowExitManagement = false };

        ClassPolicyService.ApplyTo(target, baseline: null, new ClassPolicy());
        Assert.False(target.DisableProfileEditing);
        Assert.True(target.AllowExitManagement);

        ClassPolicyService.ApplyTo(target, baseline: null, new ClassPolicy
        {
            DisableProfileClassPlanEditing = true,
            DisableSplashCustomize = true,
            AllowExitManagement = false,
        });
        Assert.True(target.DisableProfileClassPlanEditing);
        Assert.True(target.DisableSplashCustomize);
        Assert.False(target.AllowExitManagement);
    }

    [Fact]
    public void ApplyTo_WithBuiltinManagement_OnlyRestricts()
    {
        var baseline = new ManagementPolicy { DisableSettingsEditing = true, AllowExitManagement = false };
        var target = new ManagementPolicy();

        ClassPolicyService.ApplyTo(target, baseline, new ClassPolicy
        {
            DisableDebugMenu = true,
            AllowExitManagement = true,
        });

        // RemoteCI 的加严生效。
        Assert.True(target.DisableDebugMenu);
        // 集控服务器已有的限制不会被 RemoteCI 的默认值放松。
        Assert.True(target.DisableSettingsEditing);
        Assert.False(target.AllowExitManagement);
    }

    [Fact]
    public void SettingsPage_IsLockedWhenPolicyForbidsOpeningIt()
    {
        var store = new ClassPolicyStore(Path.Combine(_root, "ClassPolicy.json"));
        store.Apply(new ClassPolicy { AllowPluginSettings = false });

        var page = new RemoteCiSettingsPage(new PluginSettings(), null, store);

        Assert.Contains("RemoteCI 设置已被管理员限制", TextLabels(page.Content));
        Assert.DoesNotContain("立即推送当前课表", ButtonLabels(page.Content));
    }

    [Fact]
    public void SettingsPage_DisablesConnectionFieldsWhenPolicyForbidsChanges()
    {
        var store = new ClassPolicyStore(Path.Combine(_root, "ClassPolicy.json"));
        store.Apply(new ClassPolicy { AllowChangeConnection = false });

        var page = new RemoteCiSettingsPage(new PluginSettings(), null, store);

        // 页面本身仍可用，但连接信息输入为只读。
        Assert.Contains("立即推送当前课表", ButtonLabels(page.Content));
        var boxes = TextBoxes(page.Content);
        Assert.Contains(boxes, box => box.Text == "8765" && !box.IsEnabled);
        Assert.All(boxes.Where(box => box.Watermark?.ToString() is "端口（默认 8765）" or "云端地址，如 https://nas:8080"),
            box => Assert.False(box.IsEnabled));
    }

    private static IReadOnlyList<string> TextLabels(object? root)
    {
        var labels = new List<string>();
        Visit(root);
        return labels;

        void Visit(object? node)
        {
            if (node is TextBlock text) labels.Add(text.Text ?? string.Empty);
            if (node is ContentControl control) Visit(control.Content);
            if (node is Panel panel)
                foreach (var child in panel.Children) Visit(child);
            if (node is Border border) Visit(border.Child);
            if (node is ItemsControl items)
                foreach (var item in items.Items) Visit(item);
            if (node is ScrollViewer viewer) Visit(viewer.Content);
        }
    }

    private static IReadOnlyList<string> ButtonLabels(object? root)
    {
        var labels = new List<string>();
        Visit(root);
        return labels;

        void Visit(object? node)
        {
            if (node is Button button) labels.Add(button.Content?.ToString() ?? string.Empty);
            if (node is ContentControl control && node is not Button) Visit(control.Content);
            if (node is Panel panel)
                foreach (var child in panel.Children) Visit(child);
            if (node is Border border) Visit(border.Child);
            if (node is ScrollViewer viewer) Visit(viewer.Content);
        }
    }

    private static IReadOnlyList<TextBox> TextBoxes(object? root)
    {
        var boxes = new List<TextBox>();
        Visit(root);
        return boxes;

        void Visit(object? node)
        {
            if (node is TextBox box) boxes.Add(box);
            if (node is ContentControl control) Visit(control.Content);
            if (node is Panel panel)
                foreach (var child in panel.Children) Visit(child);
            if (node is Border border) Visit(border.Child);
            if (node is ScrollViewer viewer) Visit(viewer.Content);
        }
    }
}
