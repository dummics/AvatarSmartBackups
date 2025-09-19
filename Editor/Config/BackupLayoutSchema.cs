#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace AvatarSmartBackup.Config
{
    /// <summary>
    /// Declarative layout description for editor windows that expose Easy/Advanced modes.
    /// Blocks are listed once and tagged with the modes they should appear in, allowing the
    /// rendering code to iterate the schema instead of sprinkling conditionals across the UI.
    /// <para>
    /// To add a new block:
    /// <list type="number">
    /// <item>Add an entry to the corresponding <see cref="MainWindowBlock"/> or <see cref="RestorePreviewBlock"/> enum.</item>
    /// <item>Insert a <see cref="LayoutBlock{TBlock}"/> entry with the desired <see cref="LayoutMode"/> flags.</item>
    /// <item>Handle the new enum value inside the switch statement that renders the window.</item>
    /// </list>
    /// Keep the Easy and Advanced sequences aligned to avoid regressions. Example order:
    /// <code>
    /// // Easy tab
    /// Header → AutomaticInfo → ModeSelector → ModeSync → EasyDashboard → BodySpacing
    /// // Advanced tab
    /// Header → AutomaticInfo → ModeSelector → ModeSync → Scheduler → PrimaryActions → VersionsOverview → BodySpacing → AdvancedOverview → AdvancedSettings
    /// </code>
    /// The restore preview window follows a similar pattern where each block is tagged for the
    /// correct mode, so new blocks should follow the same procedure.
    /// </summary>
    internal static class BackupLayoutSchema
    {
        [Flags]
        internal enum LayoutMode
        {
            Easy = 1 << 0,
            Advanced = 1 << 1,
            Any = Easy | Advanced
        }

        internal readonly struct LayoutBlock<TBlock>
        {
            public LayoutBlock(TBlock id, LayoutMode modes)
            {
                Id = id;
                Modes = modes;
            }

            public TBlock Id { get; }
            public LayoutMode Modes { get; }

            public bool Supports(LayoutMode mode) => (Modes & mode) != 0;
        }

        internal enum MainWindowBlock
        {
            Header,
            AutomaticInfo,
            ModeSelector,
            ModeSync,
            EasyDashboard,
            Scheduler,
            PrimaryActions,
            VersionsOverview,
            BodySpacing,
            AdvancedOverview,
            AdvancedSettings,
        }

        internal enum RestorePreviewBlock
        {
            EasyBanner,
            HelperBanner,
            Summary,
            Filters,
            Selection,
            Files,
            Footer,
        }

        static readonly LayoutBlock<MainWindowBlock>[] s_mainWindowBlocks =
        {
            new LayoutBlock<MainWindowBlock>(MainWindowBlock.Header, LayoutMode.Any),
            new LayoutBlock<MainWindowBlock>(MainWindowBlock.AutomaticInfo, LayoutMode.Any),
            new LayoutBlock<MainWindowBlock>(MainWindowBlock.ModeSelector, LayoutMode.Any),
            new LayoutBlock<MainWindowBlock>(MainWindowBlock.ModeSync, LayoutMode.Any),
            new LayoutBlock<MainWindowBlock>(MainWindowBlock.EasyDashboard, LayoutMode.Easy),
            new LayoutBlock<MainWindowBlock>(MainWindowBlock.Scheduler, LayoutMode.Advanced),
            new LayoutBlock<MainWindowBlock>(MainWindowBlock.PrimaryActions, LayoutMode.Advanced),
            new LayoutBlock<MainWindowBlock>(MainWindowBlock.VersionsOverview, LayoutMode.Advanced),
            new LayoutBlock<MainWindowBlock>(MainWindowBlock.BodySpacing, LayoutMode.Any),
            new LayoutBlock<MainWindowBlock>(MainWindowBlock.AdvancedOverview, LayoutMode.Advanced),
            new LayoutBlock<MainWindowBlock>(MainWindowBlock.AdvancedSettings, LayoutMode.Advanced),
        };

        static readonly LayoutBlock<RestorePreviewBlock>[] s_restorePreviewBlocks =
        {
            new LayoutBlock<RestorePreviewBlock>(RestorePreviewBlock.EasyBanner, LayoutMode.Easy),
            new LayoutBlock<RestorePreviewBlock>(RestorePreviewBlock.HelperBanner, LayoutMode.Any),
            new LayoutBlock<RestorePreviewBlock>(RestorePreviewBlock.Summary, LayoutMode.Any),
            new LayoutBlock<RestorePreviewBlock>(RestorePreviewBlock.Filters, LayoutMode.Any),
            new LayoutBlock<RestorePreviewBlock>(RestorePreviewBlock.Selection, LayoutMode.Any),
            new LayoutBlock<RestorePreviewBlock>(RestorePreviewBlock.Files, LayoutMode.Any),
            new LayoutBlock<RestorePreviewBlock>(RestorePreviewBlock.Footer, LayoutMode.Any),
        };

        internal static IReadOnlyList<LayoutBlock<MainWindowBlock>> MainWindowBlocks => s_mainWindowBlocks;

        internal static IReadOnlyList<LayoutBlock<RestorePreviewBlock>> RestorePreviewBlocks => s_restorePreviewBlocks;
    }
}
#endif
