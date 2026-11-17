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

    private GalleryKind _kind = GalleryKind.All;
    private GalleryEntry? _selected;
    private bool _busy;

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

        foreach (var entry in engine.GalleryEntries)
        {
            var card = new GalleryCard(entry);
            card.Downloaded = engine.IsGalleryDownloaded(entry);
            card.Picked += (_, _) => Select(card.Entry);
            card.DoubleClick += (_, _) => Select(card.Entry);
            _cards.Add(card);
            _list.Controls.Add(card);
        }

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
        _apply = new TrayButton { Text = "Download & apply", Width = 150, Height = 34, Variant = TrayButton.Weight.Primary };
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

        _detail.Controls.AddRange([_preview, _title, _meta, _description, _credit, _source, actions, _status]);

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
        _chipRow.Location = new Point(0, Math.Max(0, (_toolbar.ClientSize.Height - _chipRow.Height) / 2));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SetKind(GalleryKind.All);
        Reload();
        Select(_engine.GalleryEntries.FirstOrDefault());
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
        // Close still closes.
        if (ActiveControl is Button) return base.ProcessCmdKey(ref msg, keyData);

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
        [.. _engine.GalleryEntries.Where(entry => entry.Matches(_search.Value, _kind))];

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

        int downloaded = _engine.GalleryEntries.Count(_engine.IsGalleryDownloaded);
        long bytes = 0;
        foreach (var entry in _engine.GalleryEntries)
        {
            if (_engine.Gallery.LocalPathIfDownloaded(entry) is { } path)
                try { bytes += new FileInfo(path).Length; } catch { /* gone between checks */ }
        }
        _count.Text = $"{_engine.GalleryEntries.Count} wallpapers · {downloaded} on disk · {BytesLabel(bytes)}";

        if (_selected is not null && !visible.Contains(_selected)) Select(visible.FirstOrDefault());
        else RefreshDetail();
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
        _credit.Text = entry.CreditLine.Length > 0 ? $"Credit: {entry.CreditLine}" : "";
        _source.Visible = entry.SourcePage.Length > 0;

        _apply.Text = downloaded ? "Apply" : "Download & apply";
        _apply.Visible = true;
        _remove.Visible = downloaded;
        _remove.Enabled = downloaded && !_engine.IsGalleryEntryInUse(entry);
        _open.Visible = downloaded;
        _status.Text = downloaded ? "" : "Not downloaded yet — press the button to fetch it.";
        _status.ForeColor = Theme.Colors.Subtle;

        if (local is not null)
        {
            bool playable = WallpaperPreview.IsVideoPath(local) && entry.Height <= 1200;
            _preview.Caption = Path.GetFileName(local);
            if (playable)
            {
                _preview.SetSource(local);
            }
            else
            {
                // A video that is too large to decode in the panel, or any image: show the still.
                string? still = _engine.Gallery.LocalThumbnailIfCached(entry);
                if (still is not null) _preview.SetSource(still);
                else _preview.SetSource(null);
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
            var path = await _engine.Gallery.EnsureThumbnailAsync(entry);
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

    /// <summary>Positions the preview, then the text under it, then the actions. The preview is
    /// always 16:9 and capped, so the pane does not grow a metre of video at a large window.</summary>
    private void LayoutDetail()
    {
        if (_detail.ClientSize.Width <= 10) return;
        int width = _detail.ClientSize.Width - DetailPadding * 2;
        int previewHeight = Math.Min(340, Math.Max(160, (int)(width * 9.0 / 16.0)));
        _preview.SetBounds(DetailPadding, DetailPadding, width, previewHeight);

        int y = DetailPadding + previewHeight + 18;
        _title.SetBounds(DetailPadding, y, width, 22);
        y += 28;
        _meta.SetBounds(DetailPadding, y, width, 16);
        y += 24;

        Place(_description, y, width);
        y = _description.Bottom + 8;
        Place(_credit, y, width);
        y = _credit.Bottom + 8;
        Place(_source, y, width);
        y = _source.Bottom + 14;

        foreach (Control control in _detail.Controls)
            if (control == _actions) _actions.Location = new Point(DetailPadding, y);
        y += 40;
        _status.SetBounds(DetailPadding, y, width, 16);
    }

    private void Place(Control control, int y, int width)
    {
        control.SetBounds(DetailPadding, y, width, control.PreferredSize.Height);
    }

    // ---- actions -----------------------------------------------------------------------

    private async void Apply(GalleryEntry entry)
    {
        if (_busy) return;
        _busy = true;
        _apply.Enabled = false;
        _status.Text = $"Downloading {entry.Title}…";
        _status.ForeColor = Theme.Colors.Subtle;
        try
        {
            var progress = new Progress<double>(p => _status.Text = $"Downloading… {p * 100:0}%");
            string path = await _engine.DownloadGalleryEntryAsync(entry, progress);
            _engine.ApplyUserSelection(path, _device);
            _status.Text = $"Applied to {_displayLabel}";
            _status.ForeColor = Theme.Colors.Accent;
            foreach (var card in _cards) card.Downloaded = _engine.IsGalleryDownloaded(card.Entry);
            RefreshDetail();
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
        var gate = new SemaphoreSlim(3);
        var tasks = _engine.GalleryEntries.Select(entry => Task.Run(async () =>
        {
            await gate.WaitAsync();
            try
            {
                if (_engine.Gallery.LocalThumbnailIfCached(entry) is { } cached)
                {
                    SetCardThumbnail(entry, cached);
                    return;
                }
                var path = await _engine.Gallery.EnsureThumbnailAsync(entry);
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
            if (IsDisposed) { bitmap.Dispose(); return; }
            _cards.FirstOrDefault(c => c.Entry == entry)?.Thumbnail = bitmap;
        });
    }

    private static string BytesLabel(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:0.#} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:0} MB",
        _ => $"{bytes / 1_000.0:0} KB",
    };
}
