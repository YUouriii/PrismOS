using System.Drawing;
using System.Windows.Forms;
using PrismOS.Config;
using PrismOS.Gallery;

namespace PrismOS.Tray;

/// <summary>The built-in gallery: a list of public-domain / CC0 wallpapers with a still for each
/// and a preview of the selected one, so picking a wallpaper does not mean downloading a
/// 200 MB file to find out what it looks like.
///
/// The tray's gallery submenu could only list names. This window shows what each entry is, how
/// big the download is, whether it is already on disk, and what it looks like — and it plays a
/// downloaded video in place rather than sending the person to the file.
///
/// A wallpaper is downloaded only when the button is pressed. Stills are fetched for the list and
/// preview, which is a few dozen kilobytes each.</summary>
public sealed class GalleryForm : Form
{
    private const int ListWidth = 372;
    private const int CardHeight = 88;
    private const int CardGap = 8;
    private const int ListPadding = 12;
    private const int DetailPadding = 20;
    private const int FooterHeight = 58;

    private readonly Engine _engine;
    private readonly string _device;
    private readonly string _displayLabel;

    private readonly List<GalleryCard> _cards = [];
    private readonly Panel _list;
    private readonly Panel _detail;
    private readonly Label _title = null!;
    private readonly Label _meta = null!;
    private readonly Label _description = null!;
    private readonly Label _credit = null!;
    private readonly LinkLabel _source = null!;
    private readonly Label _status = null!;
    private readonly Label _count = null!;
    private readonly WallpaperPreview _preview = null!;
    private readonly TrayButton _apply = null!;
    private readonly TrayButton _remove = null!;
    private readonly TrayButton _open = null!;
    private readonly SearchField _search;
    private readonly List<GalleryChip> _chips = [];
    private readonly FlowLayoutPanel _chipRow = null!;
    private readonly Panel _toolbar = null!;
    private readonly FlowLayoutPanel _actions = null!;
    private readonly ComboBox _sourceChoice = null!;
    private readonly Label _empty = null!;
    private readonly TrayButton _chooseFolder = null!;
    private readonly Label _subtitle = null!;

    /// <summary>The entries the list is currently showing: the curated manifest, or the user's
    /// folder. Rebuilt when the source changes, never mutated in place.</summary>
    private List<GalleryEntry> _entries = [];

    private GalleryKind _kind = GalleryKind.All;
    private GalleryEntry? _selected;
    private bool _busy;

    private bool LibrarySource => _sourceChoice.SelectedIndex == 1;

    public GalleryForm(Engine engine, string device, string displayLabel)
    {
        _engine = engine;
        _device = device;
        _displayLabel = displayLabel;

        Text = "Gallery";
        Icon = PrismIcon.CreateIcon();
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        ShowInTaskbar = false;
        BackColor = Theme.Colors.Window;
        ForeColor = Theme.Colors.Text;
        ClientSize = new Size(1040, 720);
        MinimumSize = new Size(880, 580);
        KeyPreview = true;

        // ---- header ---------------------------------------------------------------------
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 104,
            BackColor = Theme.Colors.Window,
            Padding = new Padding(20, 14, 20, 0),
        };
        var heading = new Label
        {
            Text = "Gallery",
            Font = new Font("Segoe UI Semibold", 15f),
            ForeColor = Theme.Colors.Text,
            AutoSize = true,
            Location = new Point(20, 12),
        };
        var subtitle = new Label
        {
            Text = "Public-domain and CC0 wallpapers. Nothing is uploaded, and a wallpaper downloads only when you ask for it.",
            ForeColor = Theme.Colors.Subtle,
            AutoSize = false,
            Size = new Size(700, 20),
            Location = new Point(20, 48),
        };
        _subtitle = subtitle;
        var applying = new Label
        {
            Text = $"Applying to: {_displayLabel}",
            ForeColor = Theme.Colors.Subtle,
            AutoSize = true,
            Location = new Point(20, 70),
        };
        header.Controls.AddRange([heading, subtitle, applying]);

        // ---- toolbar --------------------------------------------------------------------
        var toolbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = Theme.Colors.Window,
            Padding = new Padding(20, 8, 20, 8),
        };
        _toolbar = toolbar;
        _sourceChoice = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 118,
            Height = 26,
            FlatStyle = FlatStyle.Flat,
        };
        _sourceChoice.Items.AddRange(["Curated", "Your files"]);
        _sourceChoice.SelectedIndex = 0;
        Theme.StyleInput(_sourceChoice);
        _sourceChoice.SelectedIndexChanged += (_, _) => SwitchSource();

        var chipRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Colors.Window,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Margin = new Padding(0),
        };
        _chipRow = chipRow;
        foreach (var (label, kind) in new[]
                 {
                     ("All", GalleryKind.All),
                     ("Photos", GalleryKind.Photos),
                     ("Videos", GalleryKind.Videos),
                 })
        {
            var chip = new GalleryChip(label, kind);
            chip.Picked += (_, _) => SetKind(kind);
            _chips.Add(chip);
            chipRow.Controls.Add(chip);
        }

        _search = new SearchField { Width = 220 };
        _search.Changed += (_, _) => Reload();

        // Both sides are anchored rather than docked: docking a right-anchored field in a panel
        // would make it follow the left edge.
        toolbar.Controls.Add(_search);
        toolbar.Controls.Add(chipRow);
        toolbar.Controls.Add(_sourceChoice);
        _search.Location = new Point(toolbar.Width - _search.Width, 10);
        _chipRow.Location = new Point(0, 11);
        // A docked panel only learns its real width after it is added to a docking parent, so
        // positioning is done on every resize of the panel itself — the Resize event on the
        // panel, not just on the form, which fires too late.
        toolbar.Resize += (_, _) => LayoutToolbar();

        // ---- list -----------------------------------------------------------------------
        _list = new Panel
        {
            Dock = DockStyle.Left,
            Width = ListWidth,
            AutoScroll = true,
            BackColor = Theme.Colors.Window,
        };

        _empty = new Label
        {
            AutoSize = false,
            ForeColor = Theme.Colors.Subtle,
            Visible = false,
            TextAlign = ContentAlignment.TopLeft,
        };
        _list.Controls.Add(_empty);
        RebuildCards();

        // ---- detail ---------------------------------------------------------------------
        _detail = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Colors.Window,
        };

        _preview = new WallpaperPreview { Fit = FitMode.Fill };

        _title = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Font = new Font("Segoe UI Semibold", 13.5f),
            ForeColor = Theme.Colors.Text,
        };
        _meta = new Label
        {
            AutoSize = false,
            ForeColor = Theme.Colors.Subtle,
        };
        _description = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Theme.Colors.Subtle,
        };
        _credit = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Theme.Colors.Text,
        };
        _source = new LinkLabel
        {
            Text = "View source page",
            AutoSize = true,
            LinkColor = Theme.Colors.Accent,
            ActiveLinkColor = Theme.Colors.Accent,
        };

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Colors.Window,
            Margin = new Padding(0),
        };
        _actions = actions;
        _apply = new TrayButton { Text = "Set wallpaper", Width = 130, Height = 34, Variant = TrayButton.Weight.Primary };
        _apply.Click += (_, _) => { if (_selected is not null) Apply(_selected); };
        _remove = new TrayButton { Text = "Remove", Width = 96, Height = 34, Margin = new Padding(8, 0, 0, 0) };
        _remove.Click += (_, _) => { if (_selected is not null) Remove(_selected); };
        _open = new TrayButton { Text = "Show in folder", Width = 120, Height = 34, Margin = new Padding(8, 0, 0, 0) };
        _open.Click += (_, _) => { if (_selected is not null) OpenInFolder(_selected); };
        actions.Controls.AddRange([_apply, _remove, _open]);

        _status = new Label
        {
            AutoSize = false,
            ForeColor = Theme.Colors.Subtle,
        };

        _chooseFolder = new TrayButton { Text = "Choose a folder…", Width = 150, Height = 34, Visible = false };
        _chooseFolder.Click += (_, _) => ChooseFolder();

        _detail.Controls.AddRange([_preview, _title, _meta, _description, _credit, _source, actions, _status, _chooseFolder]);

        // ---- footer ---------------------------------------------------------------------
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = FooterHeight,
            Padding = new Padding(20, 12, 20, 12),
            BackColor = Theme.Colors.Window,
        };
        _count = new Label
        {
            AutoSize = true,
            ForeColor = Theme.Colors.Subtle,
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            Margin = new Padding(0, 8, 0, 0),
        };
        var close = new TrayButton
        {
            Text = "Close",
            DialogResult = DialogResult.Cancel,
            Width = 100,
            Height = 32,
        };
        footer.Controls.Add(close);
        footer.Controls.Add(_count);
        CancelButton = close;

        // Dock order: the filling body goes in first so the header, toolbar and footer take the
        // edges and the list/detail split what is left.
        Controls.Add(_detail);
        Controls.Add(_list);
        Controls.Add(toolbar);
        Controls.Add(header);
        Controls.Add(footer);

        Resize += (_, _) => { LayoutList(); LayoutToolbar(); LayoutDetail(); };
    }

    private void LayoutToolbar()
    {
        _search.Location = new Point(Math.Max(0, _toolbar.ClientSize.Width - _search.Width), 10);
        _sourceChoice.Location = new Point(0, Math.Max(0, (_toolbar.ClientSize.Height - _sourceChoice.Height) / 2));
        _chipRow.Location = new Point(_sourceChoice.Right + 10, Math.Max(0, (_toolbar.ClientSize.Height - _chipRow.Height) / 2));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SetKind(GalleryKind.All);
        Reload();
        Select(_entries.FirstOrDefault());
        _ = WarmThumbnailsAsync();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // The cards hold downscaled stills; they are per-window and go with it.
        foreach (var card in _cards) card.Thumbnail = null;
        _preview.SetSource(null);
        base.OnFormClosed(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        if (_search.Editing) return base.ProcessCmdKey(ref msg, keyData);
        // Enter and arrows belong to the buttons when a button has the focus, so Tab-ing to
        // Close still closes; the source combo keeps its own arrow keys.
        if (ActiveControl is Button or ComboBox) return base.ProcessCmdKey(ref msg, keyData);

        var visible = VisibleEntries();
        if (visible.Count == 0) return base.ProcessCmdKey(ref msg, keyData);

        int index = _selected is null ? -1 : visible.ToList().IndexOf(_selected);
        if (keyData == Keys.Down || keyData == Keys.Up)
        {
            int next = keyData == Keys.Down
                ? Math.Min(visible.Count - 1, index + 1)
                : Math.Max(0, index < 0 ? 0 : index - 1);
            Select(visible[next]);
            ScrollIntoView(visible[next]);
            return true;
        }
        if (keyData == Keys.Enter && _selected is not null)
        {
            Apply(_selected);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- list --------------------------------------------------------------------------

    private void SetKind(GalleryKind kind)
    {
        _kind = kind;
        foreach (var chip in _chips) chip.Selected = chip.Kind == kind;
        Reload();
    }

    private IReadOnlyList<GalleryEntry> VisibleEntries() =>
        [.. _entries.Where(entry => entry.Matches(_search.Value, _kind))];

    /// <summary>Stacks the matching cards and reports what is on disk. Cards are placed by hand
    /// rather than by a flow layout, which would put two entries side by side in a narrow window
    /// and cut their text in half.</summary>
    private void Reload()
    {
        var visible = VisibleEntries();
        int width = Math.Max(240, _list.ClientSize.Width - ListPadding * 2);
        int y = ListPadding;
        foreach (var entry in visible)
        {
            var card = _cards.First(c => c.Entry == entry);
            card.SetBounds(ListPadding, y, width, CardHeight);
            card.Visible = true;
            y += CardHeight + CardGap;
        }
        foreach (var card in _cards)
            if (!visible.Contains(card.Entry)) card.Visible = false;
        _list.AutoScrollMinSize = new Size(width + ListPadding * 2, y + ListPadding);

        _empty.Visible = visible.Count == 0;
        _empty.SetBounds(ListPadding, ListPadding, width, 60);
        _empty.Text = LibrarySource
            ? _engine.Config.LibraryFolder is null
                ? "No folder chosen yet. Use “Choose a folder…” on the right."
                : "No media files in this folder match the search."
            : "No wallpapers match the search.";

        if (LibrarySource)
        {
            _count.Text = $"{_entries.Count} files · {(_engine.Config.LibraryFolder is { } f ? Path.GetFileName(f) : "no folder")}";
        }
        else
        {
            int downloaded = _entries.Count(_engine.IsGalleryDownloaded);
            long bytes = 0;
            foreach (var entry in _entries)
            {
                if (_engine.Gallery.LocalPathIfDownloaded(entry) is { } path)
                    try { bytes += new FileInfo(path).Length; } catch { /* gone between checks */ }
            }
            _count.Text = $"{_entries.Count} wallpapers · {downloaded} on disk · {BytesLabel(bytes)}";
        }

        if (_selected is not null && !visible.Contains(_selected)) Select(visible.FirstOrDefault());
        else RefreshDetail();
    }

    // ---- sources -----------------------------------------------------------------------

    /// <summary>Rebuilds the card set for the current source. Cards own their downscaled stills,
    /// so the old set is disposed rather than hidden.</summary>
    private void RebuildCards()
    {
        foreach (var card in _cards)
        {
            card.Thumbnail = null;
            _list.Controls.Remove(card);
            card.Dispose();
        }
        _cards.Clear();

        _entries = LibrarySource
            ? WallpaperLibrary.Entries(_engine.Config.LibraryFolder)
            : [.. _engine.GalleryEntries];

        foreach (var entry in _entries)
        {
            var card = new GalleryCard(entry);
            card.Downloaded = _engine.IsGalleryDownloaded(entry);
            card.Picked += (_, _) => Select(card.Entry);
            card.DoubleClick += (_, _) => { Select(card.Entry); Apply(card.Entry); };
            _cards.Add(card);
            _list.Controls.Add(card);
        }
    }

    private void SwitchSource()
    {
        _subtitle.Text = LibrarySource
            ? "Your own images and videos. Nothing is copied or uploaded — Set wallpaper points the desktop straight at the file."
            : "Public-domain and CC0 wallpapers. Nothing is uploaded, and a wallpaper downloads only when you ask for it.";
        RebuildCards();
        Reload();
        Select(_entries.FirstOrDefault());
        LayoutList();
        _ = WarmThumbnailsAsync();
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder with your wallpapers",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = _engine.Config.LibraryFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _engine.Config.LibraryFolder = dialog.SelectedPath;
        _engine.SaveConfig();
        SwitchSource();
    }

    private void ScrollIntoView(GalleryEntry entry)
    {
        var card = _cards.FirstOrDefault(c => c.Entry == entry);
        if (card is not null) _list.ScrollControlIntoView(card);
    }

    private void LayoutList()
    {
        int width = Math.Max(240, _list.ClientSize.Width - ListPadding * 2);
        int y = ListPadding;
        foreach (var card in _cards)
        {
            if (!card.Visible) continue;
            card.SetBounds(ListPadding, y, width, CardHeight);
            y += CardHeight + CardGap;
        }
        _list.AutoScrollMinSize = new Size(width + ListPadding * 2, y + ListPadding);
    }

    // ---- selection and preview ---------------------------------------------------------

    private void Select(GalleryEntry? entry)
    {
        _selected = entry;
        foreach (var card in _cards) card.Selected = card.Entry == entry;
        if (entry is null)
        {
            _preview.SetSource(null);
            _title.Text = "";
            _meta.Text = "";
            _description.Text = "";
            _credit.Text = "";
            _source.Visible = false;
            _apply.Visible = false;
            _remove.Visible = false;
            _open.Visible = false;
            _status.Text = "";
            bool needsFolder = LibrarySource && _engine.Config.LibraryFolder is null;
            _chooseFolder.Visible = needsFolder;
            _actions.Visible = !needsFolder;
            _preview.Placeholder = needsFolder
                ? "Choose a folder to browse your own wallpapers."
                : "Nothing selected.";
            LayoutDetail();
            return;
        }
        RefreshDetail();
        LayoutDetail();
    }

    /// <summary>Fills the right-hand pane. A downloaded video plays in place when it is small
    /// enough for a software decode; a 4K file would cost the panel hundreds of megabytes, so it
    /// shows its still instead.</summary>
    private void RefreshDetail()
    {
        var entry = _selected;
        if (entry is null) return;

        bool downloaded = _engine.IsGalleryDownloaded(entry);
        string? local = _engine.Gallery.LocalPathIfDownloaded(entry);

        _title.Text = entry.Title;
        _meta.Text = $"{entry.KindLabel} · {entry.ResolutionLabel} · {entry.SizeLabel} · {entry.License}";
        _description.Text = entry.Description;
        _description.MaximumSize = new Size(Math.Max(120, _detail.ClientSize.Width - DetailPadding * 2), 0);
        // Your own files have nobody to credit; the curated manifest always does.
        _credit.Text = !entry.IsLocal && entry.CreditLine.Length > 0 ? $"Credit: {entry.CreditLine}" : "";
        _source.Visible = entry.SourcePage.Length > 0;

        _chooseFolder.Visible = false;
        _actions.Visible = true;
        _apply.Text = "Set wallpaper";
        _apply.Visible = true;
        _remove.Visible = downloaded && !entry.IsLocal;
        _remove.Enabled = downloaded && !_engine.IsGalleryEntryInUse(entry);
        _open.Visible = downloaded;
        _status.Text = entry.IsLocal
            ? "Your file — Set wallpaper applies it directly, nothing is copied."
            : downloaded
                ? "Ready — press Set wallpaper to put it on the desktop."
                : $"Not on this PC yet — Set wallpaper downloads the {entry.SizeLabel} first.";
        _status.ForeColor = Theme.Colors.Subtle;

        if (local is not null)
        {
            bool isVideo = WallpaperPreview.IsVideoPath(local);
            bool playable = isVideo && entry.Height <= 1200;
            _preview.Caption = Path.GetFileName(local);
            if (playable || !isVideo)
            {
                _preview.SetSource(local);
            }
            else
            {
                // A video too large to decode in the panel: show its generated still.
                string? still = _engine.Gallery.LocalThumbnailIfCached(entry);
                if (still is not null) _preview.SetSource(still);
                else
                {
                    _preview.SetSource(null);
                    _preview.Placeholder = "Making a still…";
                    _ = FetchStillAsync(entry);
                }
            }
        }
        else
        {
            _preview.Caption = "";
            string? still = _engine.Gallery.LocalThumbnailIfCached(entry);
            if (still is not null)
            {
                _preview.SetSource(still);
            }
            else
            {
                _preview.SetSource(null);
                _preview.Placeholder = "Fetching a still…";
                _ = FetchStillAsync(entry);
            }
        }
    }

    private async Task FetchStillAsync(GalleryEntry entry)
    {
        try
        {
            var path = await EnsureStillAsync(entry);
            if (path is null || IsDisposed || _selected != entry) return;
            _preview.SetSource(path);
        }
        catch (Exception ex)
        {
            if (!IsDisposed && _selected == entry)
            {
                _preview.Placeholder = "Still unavailable";
                Common.Log.Warn($"Gallery still failed for {entry.Id}: {ex.Message}");
            }
        }
    }

    private Task<string?> EnsureStillAsync(GalleryEntry entry) =>
        entry.IsLocal
            ? WallpaperLibrary.EnsureThumbnailAsync(entry, _engine.Gallery)
            : _engine.Gallery.EnsureThumbnailAsync(entry);

    /// <summary>Positions the preview, then the text under it, then the actions. The preview is
    /// always 16:9 and deliberately capped: at 340 px it pushed the action row past the bottom of
    /// the pane, where the footer clipped it. The pane is only ~550 px tall, so the preview takes
    /// what is left after the text, and never more than a third of the height.</summary>
    private void LayoutDetail()
    {
        if (_detail.ClientSize.Width <= 10) return;
        int width = _detail.ClientSize.Width - DetailPadding * 2;
        int available = Math.Max(150, _detail.ClientSize.Height - DetailPadding * 2
            - 30                     // title + gap
            - 26                     // meta + gap
            - Math.Max(0, _description.PreferredSize.Height) - 10
            - (_credit.Visible ? Math.Max(0, _credit.PreferredSize.Height) : 0) - 8
            - (_source.Visible ? 22 : 0)   // source + gap
            - 40                     // actions
            - 20                     // status + gap
        );
        int previewHeight = Math.Min(280, available);
        _preview.SetBounds(DetailPadding, DetailPadding, width, previewHeight);

        int y = DetailPadding + previewHeight + 12;
        _title.SetBounds(DetailPadding, y, width, 22);
        y += 26;
        _meta.SetBounds(DetailPadding, y, width, 16);
        y += 22;

        Place(_description, y, width);
        y = _description.Bottom + 6;
        Place(_credit, y, width);
        y = _credit.Bottom + 4;
        Place(_source, y, width);
        y = _source.Bottom + 12;

        _actions.Location = new Point(DetailPadding, y);
        _chooseFolder.Location = new Point(DetailPadding, y);
        y += 38;
        _status.SetBounds(DetailPadding, y, width, 16);
    }

    private void Place(Control control, int y, int width)
    {
        // A hidden row (no credit, no source link) must not reserve its height, or the pane
        // grows a blank band between the description and the buttons.
        int height = control.Visible ? control.PreferredSize.Height : 0;
        control.SetBounds(DetailPadding, y, width, height);
    }

    // ---- actions -----------------------------------------------------------------------

    private async void Apply(GalleryEntry entry)
    {
        if (_busy) return;
        _busy = true;
        _apply.Enabled = false;
        _status.Text = entry.IsLocal ? $"Applying {entry.Title}…" : $"Downloading {entry.Title}…";
        _status.ForeColor = Theme.Colors.Subtle;
        try
        {
            var progress = new Progress<double>(p => _status.Text = $"Downloading… {p * 100:0}%");
            string path = await _engine.DownloadGalleryEntryAsync(entry, progress);
            _engine.ApplyUserSelection(path, _device);
            foreach (var card in _cards) card.Downloaded = _engine.IsGalleryDownloaded(card.Entry);
            RefreshDetail();
            _status.Text = $"Applied to {_displayLabel}";
            _status.ForeColor = Theme.Colors.Accent;
            _ = WarmThumbnailsAsync();
        }
        catch (Exception ex)
        {
            Common.Log.Error($"Gallery download failed: {entry.Id}", ex);
            _status.Text = $"Failed: {ex.Message}";
            _status.ForeColor = Theme.Colors.Subtle;
        }
        finally
        {
            _busy = false;
            _apply.Enabled = true;
        }
    }

    private void Remove(GalleryEntry entry)
    {
        if (_engine.IsGalleryEntryInUse(entry))
        {
            _status.Text = "In use as the current wallpaper — apply something else first.";
            return;
        }
        if (_engine.Gallery.DeleteDownload(entry))
        {
            foreach (var card in _cards) card.Downloaded = _engine.IsGalleryDownloaded(card.Entry);
            _status.Text = "Removed the downloaded copy.";
            RefreshDetail();
        }
    }

    private void OpenInFolder(GalleryEntry entry)
    {
        if (_engine.Gallery.LocalPathIfDownloaded(entry) is not { } path) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Common.Log.Warn($"Could not open the gallery folder: {ex.Message}"); }
    }

    // ---- stills ------------------------------------------------------------------------

    /// <summary>Keeps the cards' stills coming without holding up the window: three at a time, and
    /// each one painted as it lands rather than after all of them are done.</summary>
    private async Task WarmThumbnailsAsync()
    {
        var entries = _entries;
        var gate = new SemaphoreSlim(3);
        var tasks = entries.Select(entry => Task.Run(async () =>
        {
            await gate.WaitAsync();
            try
            {
                if (_engine.Gallery.LocalThumbnailIfCached(entry) is { } cached)
                {
                    SetCardThumbnail(entry, cached);
                    return;
                }
                var path = await EnsureStillAsync(entry);
                if (path is not null) SetCardThumbnail(entry, path);
            }
            catch (Exception ex)
            {
                Common.Log.Warn($"Gallery still failed for {entry.Id}: {ex.Message}");
            }
            finally { gate.Release(); }
        })).ToArray();

        try { await Task.WhenAll(tasks); } catch { /* a still is optional */ }
    }

    private void SetCardThumbnail(GalleryEntry entry, string path)
    {
        if (IsDisposed) return;
        var bitmap = GalleryImages.Load(path, 240);
        if (bitmap is null) return;
        BeginInvoke(() =>
        {
            var card = IsDisposed ? null : _cards.FirstOrDefault(c => c.Entry == entry);
            if (card is null) { bitmap.Dispose(); return; }   // source switched while it loaded
            card.Thumbnail = bitmap;
        });
    }

    private static string BytesLabel(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:0.#} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:0} MB",
        _ => $"{bytes / 1_000.0:0} KB",
    };
}
