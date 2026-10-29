using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Noesis;
using UnityEngine;

namespace SeaPowerAIAdvisor.Translation;

// Track screen-view lifetimes once; ordinary frames do not enumerate every scene object or UI node.
internal sealed class ScreenTranslationDriver : MonoBehaviour
{
    private static ScreenTranslationDriver? current;
    private static readonly PropertyInfo initialized = typeof(NoesisView).Assembly.GetType("NoesisUnity", true)!.GetProperty("Initialized", BindingFlags.Public | BindingFlags.Static)!;
    private readonly List<NoesisView> views = new();
    private Func<bool> isEnabled = null!;
    private Func<string, string> text = null!;
    private Func<Exception, string> message = null!;
    private Func<TranslationBatch, CancellationToken, Task<TranslationResult>> translate = null!;
    private Action<Exception> report = null!;
    private Harmony? patches;
    private TranslationOverlay? overlay;
    private FrameworkElement? root;
    private Point? position;
    private bool scanned, failed;
    internal void Initialize(string patchId, Func<bool> enabled, Func<string, string> text, Func<Exception, string> message,
        Func<TranslationBatch, CancellationToken, Task<TranslationResult>> translate, Action<Exception> report)
    {
        isEnabled = enabled; this.text = text; this.message = message; this.translate = translate; this.report = report; current = this;
        patches = new Harmony(patchId);
        foreach (var method in new[] { "LoadXaml", "OnEnable", "OnDisable", "OnDestroy" })
            patches.Patch(AccessTools.Method(typeof(NoesisView), method) ?? throw new MissingMethodException(typeof(NoesisView).FullName, method),
                postfix: new HarmonyMethod(typeof(ScreenTranslationDriver), nameof(Changed)));
    }
    private static void Changed(NoesisView __instance)
    { if (current != null && !current.views.Contains(__instance)) current.views.Add(__instance); }
    private void Update()
    {
        if (failed) return;
        try
        {
            if (!(bool)initialized.GetValue(null)!) return;
            if (!scanned) { foreach (var view in Resources.FindObjectsOfTypeAll<NoesisView>()) Changed(view); scanned = true; }
            views.RemoveAll(v => v == null);
            var active = isEnabled() ? views.Where(v => v.isActiveAndEnabled && v.gameObject.activeInHierarchy && !v.WorldSpace &&
                !v.IsRenderToTexture() && v.EnableMouse && v.Content != null && v.Content.ActualWidth > 0 && v.Content.ActualHeight > 0)
                .Where(v => v.GetComponent<Camera>() is not { } c || c.targetDisplay == 0)
                .OrderByDescending(v => v.GetComponent<Camera>() is { } c ? c.depth : float.MaxValue).FirstOrDefault() : null;
            var next = active?.Content;
            if (!ReferenceEquals(root, next))
            {
                overlay?.Dispose(); overlay = null; root = next;
                if (root != null) overlay = new TranslationOverlay(root, text, message, translate, report, position, p => position = p);
            }
            overlay?.Tick();
        }
        catch (Exception error) { failed = true; report(error); overlay?.Dispose(); overlay = null; root = null; views.Clear(); }
    }
    private void OnDestroy()
    {
        overlay?.Dispose(); overlay = null; patches?.UnpatchSelf(); if (ReferenceEquals(current, this)) current = null;
    }
}
