using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using NAudio.Wave;

namespace Bop;

public class PlayerForm : Form
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilter(uint msg, uint flags);

    private const int HOTKEY_ID = 9000;
    private const int WM_HOTKEY = 0x0312;
    private const uint VK_MEDIA_PLAY_PAUSE = 0xCD;
    private const uint VK_F8 = 0x77;

    public static Icon? GetEmbeddedIcon(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"Bop.{resourceName}");
        return stream != null ? new Icon(stream) : null;
    }

    private readonly Action<float> _onVolumeChanged;
    private readonly Action<double> _onSeekRequested;
    private readonly Action _onPlayPauseToggled;
    private readonly Action _onStopRequested;
    private readonly Action? _onAddRequested;
    private readonly Action? _onSkipRequested;
    private readonly Action<Guid>? _onRemoveRequested;

    private WaveOutEvent? _outputDevice;
    private MediaFoundationReader? _audioFile;
    private string _songTitle = "Title of the video";
    private string _artistName = "YouTube channel";
    private bool _isUserSeeking = false;
    private bool _isUserVoluming = false;
    private double _currentProgress = 0.0;
    private float _currentVolume = 0.5f;
    private Image? _coverImage = null;
    private Bitmap? _blurredBanner = null;
    private Bitmap? _sharpBanner = null;
    private static readonly HttpClient _httpClient = new();
    private bool _isDarkMode = false;
    private bool _isHoveringBanner = false;

    // Incrémenté à chaque changement de piste : permet à LoadThumbnailAsync de
    // détecter et ignorer une réponse réseau arrivée en retard (piste déjà changée).
    private int _thumbnailRequestVersion = 0;

    private List<QueueItem> _queue = new();
    private int _hoveredQueueIndex = -1;

    private Color _bgColor;
    private Color _primaryTextColor;
    private Color _secondaryTextColor;
    private Color _trackBgColor;
    private Color _coverBgColor;
    private Color _coverBgColor2;
    private readonly Color _accentColor = Color.FromArgb(151, 125, 255);

    private const int WM_APPCOMMAND = 0x0319;
    private const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;

    private const int BannerHeight = 190;
    private Rectangle _bannerRect;
    private Rectangle _playBtnBounds;
    private Rectangle _prevBtnBounds; // -5s
    private Rectangle _nextBtnBounds; // +5s
    private Rectangle _skipBtnBounds; // Next (morceau suivant)
    private Rectangle _closeBtnBounds;
    private Rectangle _seekBarBounds;
    private Rectangle _volumeBarBounds;
    private Rectangle _addBtnBounds;

    private enum HoverTarget { None, Play, Prev, Next, Skip, Close, Add, QueueRemove }
    private HoverTarget _hoveredButton = HoverTarget.None;

    private readonly System.Windows.Forms.Timer _updateTimer;

    public PlayerForm(
        Action<float> onVolumeChanged,
        Action<double> onSeekRequested,
        Action onPlayPauseToggled,
        Action onStopRequested,
        Action? onAddRequested = null,
        Action? onSkipRequested = null,
        Action<Guid>? onRemoveRequested = null)
    {
        _onVolumeChanged = onVolumeChanged;
        _onSeekRequested = onSeekRequested;
        _onPlayPauseToggled = onPlayPauseToggled;
        _onStopRequested = onStopRequested;
        _onAddRequested = onAddRequested;
        _onSkipRequested = onSkipRequested;
        _onRemoveRequested = onRemoveRequested;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(340, 365); // Hauteur initiale compacte
        TopMost = true;
        DoubleBuffered = true;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        
        Text = "Bop";

        var primaryScreen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        Location = new Point(primaryScreen.Right - Width - 30, primaryScreen.Bottom - Height - 30);

        _bannerRect = new Rectangle(0, 0, Width, BannerHeight);
        _closeBtnBounds = new Rectangle(Width - 40, 14, 26, 26);

        _playBtnBounds = new Rectangle((Width - 56) / 2, 118, 56, 56);
        _prevBtnBounds = new Rectangle(_playBtnBounds.X - 36 - 10, 128, 36, 36); // -5s
        _nextBtnBounds = new Rectangle(_playBtnBounds.Right + 10, 128, 36, 36);  // +5s
        _skipBtnBounds = new Rectangle(_nextBtnBounds.Right + 8, 128, 36, 36);   // Next

        _seekBarBounds = new Rectangle(25, 200, Width - 50, 14);
        _volumeBarBounds = new Rectangle(65, 300, Width - 130, 14);
        _addBtnBounds = new Rectangle(Width - 45, 323, 24, 24);

        RecalculateFormHeight();
        UpdateThemeColors();
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;

        _updateTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _updateTimer.Tick += UpdateProgress;

        MouseDown += PlayerForm_MouseDown;
        MouseUp += PlayerForm_MouseUp;
        MouseMove += PlayerForm_MouseMove;
        MouseLeave += PlayerForm_MouseLeave;

        var icon = GetEmbeddedIcon("gmalalatete.ico");
        this.Icon = icon ?? SystemIcons.Application;
    }

    public void UpdateQueue(List<QueueItem> queue)
    {
        _queue = new List<QueueItem>(queue);
        RecalculateFormHeight();
        Invalidate();
    }

    private void RecalculateFormHeight()
    {
        int visibleCount = Math.Min(_queue.Count, 6);
        int baseHeight = 365; // Hauteur sans éléments dans la liste
        int targetHeight = baseHeight + (visibleCount * 26);

        if (Height != targetHeight)
        {
            int diff = targetHeight - Height;
            Size = new Size(Width, targetHeight);
            Location = new Point(Location.X, Location.Y - diff); // Ajuste la position pour agrandir par le haut/bas proprement
            ApplyRoundedRegion(24);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ChangeWindowMessageFilter(WM_HOTKEY, 1);
        bool registered = RegisterHotKey(this.Handle, HOTKEY_ID, 0x0000, VK_MEDIA_PLAY_PAUSE);
        if (!registered)
        {
            const uint MOD_CONTROL = 0x0002;
            RegisterHotKey(this.Handle, HOTKEY_ID, MOD_CONTROL, VK_F8);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
        {
            _onPlayPauseToggled();
            Invalidate();
            return;
        }
        
        if (m.Msg == WM_APPCOMMAND)
        {
            int cmd = (int)((m.LParam.ToInt64() >> 16) & 0xFFFF);
            if (cmd == APPCOMMAND_MEDIA_PLAY_PAUSE)
            {
                _onPlayPauseToggled();
                Invalidate();
                return;
            }
        }

        base.WndProc(ref m);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotKey(this.Handle, HOTKEY_ID);
        base.OnHandleDestroyed(e);
    }

    private bool IsWindowsInDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var registryValue = key?.GetValue("AppsUseLightTheme");
            if (registryValue is int value)
            {
                return value == 0;
            }
        }
        catch { }
        return false;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        UnregisterHotKey(this.Handle, HOTKEY_ID);
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;

        _coverImage?.Dispose();
        _coverImage = null;
        _blurredBanner?.Dispose();
        _blurredBanner = null;
        _sharpBanner?.Dispose();
        _sharpBanner = null;

        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnFormClosing(e);
    }

    private void UpdateThemeColors()
    {
        _isDarkMode = IsWindowsInDarkMode();

        if (_isDarkMode)
        {
            _bgColor = Color.FromArgb(24, 24, 28);
            _primaryTextColor = Color.FromArgb(248, 248, 252);
            _secondaryTextColor = Color.FromArgb(165, 165, 178);
            _trackBgColor = Color.FromArgb(58, 58, 68);
            _coverBgColor = Color.FromArgb(94, 74, 165);
            _coverBgColor2 = Color.FromArgb(58, 44, 110);
        }
        else
        {
            _bgColor = Color.FromArgb(250, 250, 252);
            _primaryTextColor = Color.FromArgb(30, 30, 38);
            _secondaryTextColor = Color.FromArgb(115, 115, 128);
            _trackBgColor = Color.FromArgb(222, 222, 228);
            _coverBgColor = Color.FromArgb(196, 178, 255);
            _coverBgColor2 = Color.FromArgb(150, 130, 235);
        }

        BackColor = _bgColor;
        Invalidate();
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle)
        {
            UpdateThemeColors();
        }
    }

    private void ApplyRoundedRegion(int radius)
    {
        using GraphicsPath path = GetRoundedRectPath(new Rectangle(0, 0, Width, Height), radius);
        Region = new Region(path);
    }

    public void BindMedia(
        string title,
        string channelName = "YouTube Channel",
        WaveOutEvent? outputDevice = null,
        MediaFoundationReader? audioFile = null,
        float currentVolume = 0.5f,
        string? thumbnailUrl = null)
    {
        _songTitle = title;
        _artistName = channelName;
        _outputDevice = outputDevice;
        _audioFile = audioFile;
        _currentVolume = currentVolume;

        _thumbnailRequestVersion++; // toute requête de miniature en vol devient obsolète
        _coverImage?.Dispose();
        _coverImage = null;
        _blurredBanner?.Dispose();
        _blurredBanner = null;
        _sharpBanner?.Dispose();
        _sharpBanner = null;

        if (!string.IsNullOrEmpty(thumbnailUrl))
        {
            _ = LoadThumbnailAsync(thumbnailUrl);
        }

        _updateTimer.Start();
        if (!Visible) Show();
        BringToFront();
        Invalidate();
    }

    private async Task LoadThumbnailAsync(string url)
    {
        int myVersion = ++_thumbnailRequestVersion;
        try
        {
            byte[] bytes = await _httpClient.GetByteArrayAsync(url);

            // Une piste plus récente a été chargée pendant ce téléchargement : on jette le résultat.
            if (myVersion != _thumbnailRequestVersion) return;

            using MemoryStream ms = new MemoryStream(bytes);
            using Image downloadedImg = Image.FromStream(ms); // évite la fuite du bitmap original après Clone()

            Image newCover = (Image)downloadedImg.Clone();
            Bitmap newBlurred = CreateBlurredBanner(newCover, _bannerRect.Width, _bannerRect.Height);
            Bitmap newSharp = CreateSharpBanner(newCover, _bannerRect.Width, _bannerRect.Height);

            // Revérifier après les traitements (potentiellement coûteux) : toujours d'actualité ?
            if (myVersion != _thumbnailRequestVersion)
            {
                newCover.Dispose();
                newBlurred.Dispose();
                newSharp.Dispose();
                return;
            }

            _coverImage?.Dispose();
            _blurredBanner?.Dispose();
            _sharpBanner?.Dispose();
            _coverImage = newCover;
            _blurredBanner = newBlurred;
            _sharpBanner = newSharp;

            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(new Action(Invalidate));
            }
        }
        catch
        {
            if (myVersion == _thumbnailRequestVersion)
            {
                _coverImage = null;
                _blurredBanner = null;
                _sharpBanner = null;
            }
        }
    }

    private Bitmap CreateSharpBanner(Image source, int width, int height)
    {
        float imgRatio = (float)source.Width / source.Height;
        float rectRatio = (float)width / height;
        Rectangle srcRect;
        if (imgRatio > rectRatio)
        {
            int cropWidth = (int)(source.Height * rectRatio);
            srcRect = new Rectangle((source.Width - cropWidth) / 2, 0, cropWidth, source.Height);
        }
        else
        {
            int cropHeight = (int)(source.Width / rectRatio);
            srcRect = new Rectangle(0, (source.Height - cropHeight) / 2, source.Width, cropHeight);
        }

        var result = new Bitmap(width, height);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, width, height), srcRect, GraphicsUnit.Pixel);
        }
        return result;
    }

    private Bitmap CreateBlurredBanner(Image source, int width, int height, int blurFactor = 14)
    {
        int smallW = Math.Max(1, width / blurFactor);
        int smallH = Math.Max(1, height / blurFactor);

        float imgRatio = (float)source.Width / source.Height;
        float rectRatio = (float)width / height;
        Rectangle srcRect;
        if (imgRatio > rectRatio)
        {
            int cropWidth = (int)(source.Height * rectRatio);
            srcRect = new Rectangle((source.Width - cropWidth) / 2, 0, cropWidth, source.Height);
        }
        else
        {
            int cropHeight = (int)(source.Width / rectRatio);
            srcRect = new Rectangle(0, (source.Height - cropHeight) / 2, source.Width, cropHeight);
        }

        using var small = new Bitmap(smallW, smallH);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, smallW, smallH), srcRect, GraphicsUnit.Pixel);
        }

        var result = new Bitmap(width, height);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawImage(small, 0, 0, width, height);

            using var dim = new SolidBrush(Color.FromArgb(40, 0, 0, 0));
            g.FillRectangle(dim, 0, 0, width, height);
        }
        return result;
    }

    public void SetLoadingState(string statusText)
    {
        _songTitle = statusText;
        _artistName = "Loading...";
        _currentProgress = 0;

        _thumbnailRequestVersion++; // toute requête de miniature en vol devient obsolète
        _coverImage?.Dispose();
        _coverImage = null;
        _blurredBanner?.Dispose();
        _blurredBanner = null;
        _sharpBanner?.Dispose();
        _sharpBanner = null;

        if (!Visible) Show();
        BringToFront();
        Invalidate();
    }

    private bool _isDragging = false;
    private Point _dragCursorPoint;
    private Point _dragFormPoint;

    private void PlayerForm_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_closeBtnBounds.Contains(e.Location))
            {
                Hide();
                return;
            }

            if (_addBtnBounds.Contains(e.Location))
            {
                _onAddRequested?.Invoke();
                return;
            }

            if (_hoveredQueueIndex >= 0 && _hoveredQueueIndex < _queue.Count)
            {
                int itemY = 357 + (_hoveredQueueIndex * 26);
                Rectangle deleteBtnRect = new Rectangle(Width - 55, itemY, 20, 20);

                if (deleteBtnRect.Contains(e.Location))
                {
                    _onRemoveRequested?.Invoke(_queue[_hoveredQueueIndex].Id);
                    return;
                }
            }

            if (_isHoveringBanner)
            {
                if (_playBtnBounds.Contains(e.Location))
                {
                    _onPlayPauseToggled();
                    Invalidate();
                    return;
                }

                if (_prevBtnBounds.Contains(e.Location))
                {
                    SeekRelative(-5);
                    return;
                }

                if (_nextBtnBounds.Contains(e.Location))
                {
                    SeekRelative(5);
                    return;
                }

                if (_skipBtnBounds.Contains(e.Location))
                {
                    _onSkipRequested?.Invoke();
                    return;
                }
            }

            if (_seekBarBounds.Contains(e.Location))
            {
                _isUserSeeking = true;
                HandleSeek(e.X);
                return;
            }

            if (_volumeBarBounds.Contains(e.Location))
            {
                _isUserVoluming = true;
                HandleVolume(e.X);
                return;
            }

            _isDragging = true;
            _dragCursorPoint = Cursor.Position;
            _dragFormPoint = Location;
        }
    }

    private void PlayerForm_MouseMove(object? sender, MouseEventArgs e)
    {
        bool wasHoveringBanner = _isHoveringBanner;
        _isHoveringBanner = _bannerRect.Contains(e.Location);

        if (_isDragging)
        {
            Point dif = Point.Subtract(Cursor.Position, new Size(_dragCursorPoint));
            Location = Point.Add(_dragFormPoint, new Size(dif));
        }
        else if (_isUserSeeking)
        {
            HandleSeek(e.X);
        }
        else if (_isUserVoluming)
        {
            HandleVolume(e.X);
        }

        int oldQueueIndex = _hoveredQueueIndex;
        _hoveredQueueIndex = -1;
        int itemY = 357;
        for (int i = 0; i < Math.Min(_queue.Count, 6); i++)
        {
            Rectangle rowRect = new Rectangle(20, itemY, Width - 40, 24);
            if (rowRect.Contains(e.Location))
            {
                _hoveredQueueIndex = i;
                break;
            }
            itemY += 26;
        }

        var newHover = HoverTarget.None;
        if (_closeBtnBounds.Contains(e.Location)) newHover = HoverTarget.Close;
        else if (_addBtnBounds.Contains(e.Location)) newHover = HoverTarget.Add;
        else if (_hoveredQueueIndex >= 0) newHover = HoverTarget.QueueRemove;
        else if (_isHoveringBanner)
        {
            if (_playBtnBounds.Contains(e.Location)) newHover = HoverTarget.Play;
            else if (_prevBtnBounds.Contains(e.Location)) newHover = HoverTarget.Prev;
            else if (_nextBtnBounds.Contains(e.Location)) newHover = HoverTarget.Next;
            else if (_skipBtnBounds.Contains(e.Location)) newHover = HoverTarget.Skip;
        }

        if (newHover != _hoveredButton || wasHoveringBanner != _isHoveringBanner || oldQueueIndex != _hoveredQueueIndex)
        {
            _hoveredButton = newHover;
            Cursor = newHover != HoverTarget.None ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    private void PlayerForm_MouseLeave(object? sender, EventArgs e)
    {
        if (_isHoveringBanner || _hoveredButton != HoverTarget.None || _hoveredQueueIndex != -1)
        {
            _isHoveringBanner = false;
            _hoveredButton = HoverTarget.None;
            _hoveredQueueIndex = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    private void PlayerForm_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _isDragging = false;
            if (_isUserSeeking)
            {
                _isUserSeeking = false;
                _onSeekRequested(_currentProgress);
            }
            if (_isUserVoluming)
            {
                _isUserVoluming = false;
            }
        }
    }

    private void HandleSeek(int mouseX)
    {
        int relativeX = Math.Clamp(mouseX - _seekBarBounds.X, 0, _seekBarBounds.Width);
        _currentProgress = (double)relativeX / _seekBarBounds.Width;
        Invalidate();
    }

    private void HandleVolume(int mouseX)
    {
        int relativeX = Math.Clamp(mouseX - _volumeBarBounds.X, 0, _volumeBarBounds.Width);
        _currentVolume = (float)relativeX / _volumeBarBounds.Width;
        _onVolumeChanged(_currentVolume);
        Invalidate();
    }

    private void SeekRelative(double seconds)
    {
        if (_audioFile == null) return;
        var newTime = _audioFile.CurrentTime.Add(TimeSpan.FromSeconds(seconds));
        if (newTime < TimeSpan.Zero) newTime = TimeSpan.Zero;
        if (newTime > _audioFile.TotalTime) newTime = _audioFile.TotalTime;
        _audioFile.CurrentTime = newTime;
    }

    private void UpdateProgress(object? sender, EventArgs e)
    {
        if (_audioFile != null && !_isUserSeeking && _audioFile.TotalTime.TotalSeconds > 0)
        {
            _currentProgress = _audioFile.CurrentTime.TotalSeconds / _audioFile.TotalTime.TotalSeconds;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        g.Clear(_bgColor);

        DrawBanner(g);
        DrawBannerControls(g);
        DrawProgressRow(g);
        DrawTitleBlock(g);
        DrawVolumeRow(g);
        DrawQueueSection(g);
    }

    private void DrawBanner(Graphics g)
    {
        using GraphicsPath bannerPath = GetTopRoundedRectPath(_bannerRect, 24);
        GraphicsState state = g.Save();
        g.SetClip(bannerPath);

        if (_isHoveringBanner && _blurredBanner != null)
        {
            g.DrawImage(_blurredBanner, _bannerRect);
        }
        else if (!_isHoveringBanner && _sharpBanner != null)
        {
            g.DrawImage(_sharpBanner, _bannerRect);
        }
        else
        {
            using var placeholder = new LinearGradientBrush(
                _bannerRect, _coverBgColor, _coverBgColor2, LinearGradientMode.ForwardDiagonal);
            g.FillRectangle(placeholder, _bannerRect);
            DrawMusicNote(g, new Point(_bannerRect.Width / 2, _bannerRect.Height / 2 - 20));
        }

        if (_isHoveringBanner)
        {
            using var overlay = new LinearGradientBrush(
                _bannerRect,
                Color.FromArgb(10, 0, 0, 0),
                Color.FromArgb(150, 0, 0, 0),
                LinearGradientMode.Vertical);
            var blend = new ColorBlend
            {
                Colors = new[]
                {
                    Color.FromArgb(0, 0, 0, 0),
                    Color.FromArgb(20, 0, 0, 0),
                    Color.FromArgb(170, 0, 0, 0)
                },
                Positions = new[] { 0f, 0.45f, 1f }
            };
            overlay.InterpolationColors = blend;
            g.FillRectangle(overlay, _bannerRect);
        }

        g.Restore(state);
    }

    private void DrawBannerControls(Graphics g)
    {
        bool closeHover = _hoveredButton == HoverTarget.Close;
        using (SolidBrush closeBg = new SolidBrush(Color.FromArgb(closeHover ? 90 : 55, 0, 0, 0)))
        {
            g.FillEllipse(closeBg, _closeBtnBounds);
        }
        using (Pen closePen = new Pen(Color.White, 1.6f))
        {
            int pad = 8;
            g.DrawLine(closePen, _closeBtnBounds.X + pad, _closeBtnBounds.Y + pad, _closeBtnBounds.Right - pad, _closeBtnBounds.Bottom - pad);
            g.DrawLine(closePen, _closeBtnBounds.Right - pad, _closeBtnBounds.Y + pad, _closeBtnBounds.X + pad, _closeBtnBounds.Bottom - pad);
        }

        if (_isHoveringBanner)
        {
            DrawGlassCircle(g, _prevBtnBounds, _hoveredButton == HoverTarget.Prev);
            DrawCircularArrow(g, _prevBtnBounds, Color.White, isForward: false);

            DrawGlassCircle(g, _nextBtnBounds, _hoveredButton == HoverTarget.Next);
            DrawCircularArrow(g, _nextBtnBounds, Color.White, isForward: true);

            DrawGlassCircle(g, _skipBtnBounds, _hoveredButton == HoverTarget.Skip);
            DrawNextTrackIcon(g, _skipBtnBounds, Color.White);

            bool playHover = _hoveredButton == HoverTarget.Play;
            int growth = playHover ? 2 : 0;
            Rectangle playCircle = Rectangle.Inflate(_playBtnBounds, growth, growth);
            using (SolidBrush playBg = new SolidBrush(Color.White))
            {
                g.FillEllipse(playBg, playCircle);
            }

            bool isPlaying = _outputDevice != null && _outputDevice.PlaybackState == PlaybackState.Playing;
            Color iconColor = Color.FromArgb(20, 20, 24);
            if (isPlaying)
                DrawPauseIcon(g, playCircle, iconColor);
            else
                DrawPlayIcon(g, playCircle, iconColor);
        }
    }

    private void DrawGlassCircle(Graphics g, Rectangle r, bool hovered)
    {
        using SolidBrush b = new SolidBrush(Color.FromArgb(hovered ? 55 : 28, 255, 255, 255));
        g.FillEllipse(b, r);
    }

    private void DrawProgressRow(Graphics g)
    {
        using (Pen bgTrackPen = new Pen(_trackBgColor, 3))
        using (Pen fillTrackPen = new Pen(_accentColor, 3))
        {
            int yTrack = _seekBarBounds.Y + 5;
            g.DrawLine(bgTrackPen, _seekBarBounds.X, yTrack, _seekBarBounds.Right, yTrack);

            int progressWidth = (int)(_seekBarBounds.Width * Math.Clamp(_currentProgress, 0.0, 1.0));
            if (progressWidth > 0)
            {
                g.DrawLine(fillTrackPen, _seekBarBounds.X, yTrack, _seekBarBounds.X + progressWidth, yTrack);
            }

            using SolidBrush thumbBrush = new SolidBrush(_accentColor);
            g.FillEllipse(thumbBrush, _seekBarBounds.X + progressWidth - 5, yTrack - 5, 10, 10);
        }

        TimeSpan currentTS = _audioFile != null ? _audioFile.CurrentTime : TimeSpan.Zero;
        TimeSpan totalTS = _audioFile != null ? _audioFile.TotalTime : TimeSpan.Zero;

        using Font timeFont = new Font("Segoe UI", 8f, FontStyle.Regular);
        using SolidBrush timeBrush = new SolidBrush(_secondaryTextColor);
        int yTime = _seekBarBounds.Bottom - 2;
        g.DrawString($"{currentTS:mm\\:ss}", timeFont, timeBrush, _seekBarBounds.X, yTime);
        StringFormat sfRight = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString($"{totalTS:mm\\:ss}", timeFont, timeBrush, _seekBarBounds.Right, yTime, sfRight);
    }

    private void DrawTitleBlock(Graphics g)
    {
        using Font titleFont = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
        using Font artistFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        using SolidBrush titleBrush = new SolidBrush(_primaryTextColor);
        using SolidBrush artistBrush = new SolidBrush(_secondaryTextColor);

        StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(_songTitle, titleFont, titleBrush, new RectangleF(15, 236, Width - 30, 24), sf);
        g.DrawString(_artistName, artistFont, artistBrush, new RectangleF(15, 260, Width - 30, 18), sf);
    }

    private void DrawVolumeRow(Graphics g)
    {
        DrawSpeakerIcon(g, new Point(_volumeBarBounds.X - 22, _volumeBarBounds.Y - 2), _secondaryTextColor, isHigh: false);

        using (Pen bgVolPen = new Pen(_trackBgColor, 3))
        using (Pen fillVolPen = new Pen(_accentColor, 3))
        {
            int yVol = _volumeBarBounds.Y + 5;
            g.DrawLine(bgVolPen, _volumeBarBounds.X, yVol, _volumeBarBounds.Right, yVol);

            int volWidth = (int)(_volumeBarBounds.Width * Math.Clamp(_currentVolume, 0.0f, 1.0f));
            if (volWidth > 0)
            {
                g.DrawLine(fillVolPen, _volumeBarBounds.X, yVol, _volumeBarBounds.X + volWidth, yVol);
            }

            using SolidBrush volThumb = new SolidBrush(_accentColor);
            g.FillEllipse(volThumb, _volumeBarBounds.X + volWidth - 7, yVol - 7, 14, 14);
        }

        DrawSpeakerIcon(g, new Point(_volumeBarBounds.Right + 8, _volumeBarBounds.Y - 2), _secondaryTextColor, isHigh: true);
    }

    private void DrawQueueSection(Graphics g)
    {
        int startY = 325;
        string headerText = "Queue";

        using (Font headerFont = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold))
        using (SolidBrush textBrush = new SolidBrush(_secondaryTextColor))
        using (Pen linePen = new Pen(_trackBgColor, 1))
        {
            // Zone réservée pour la section (du bord gauche 20px jusqu'au bouton +)
            int leftMargin = 20;
            int rightMargin = _addBtnBounds.X - 10;
            int availableWidth = rightMargin - leftMargin;

            SizeF textSize = g.MeasureString(headerText, headerFont);
            int textX = leftMargin + (int)((availableWidth - textSize.Width) / 2);
            int lineY = startY + (int)(textSize.Height / 2);

            // Ligne gauche
            g.DrawLine(linePen, leftMargin, lineY, textX - 10, lineY);
            // Texte "Queue" centré
            g.DrawString(headerText, headerFont, textBrush, textX, startY);
            // Ligne droite
            g.DrawLine(linePen, textX + (int)textSize.Width + 10, lineY, rightMargin, lineY);
        }

        // Bouton "+"
        bool addHover = _hoveredButton == HoverTarget.Add;
        using (SolidBrush addBg = new SolidBrush(Color.FromArgb(addHover ? 80 : 40, _accentColor)))
        using (Pen addPen = new Pen(_accentColor, 2))
        {
            g.FillEllipse(addBg, _addBtnBounds);
            g.DrawLine(addPen, _addBtnBounds.X + 6, _addBtnBounds.Y + 12, _addBtnBounds.Right - 6, _addBtnBounds.Y + 12);
            g.DrawLine(addPen, _addBtnBounds.X + 12, _addBtnBounds.Y + 6, _addBtnBounds.X + 12, _addBtnBounds.Bottom - 6);
        }

        // Rendu des éléments de la file d'attente s'il y en a
        int itemY = startY + 32;
        using Font titleFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        using Font durationFont = new Font("Segoe UI", 9f, FontStyle.Regular);
        using SolidBrush titleBrush = new SolidBrush(_primaryTextColor);
        using SolidBrush durationBrush = new SolidBrush(_secondaryTextColor);
        using Pen deletePen = new Pen(Color.FromArgb(235, 87, 87), 1.8f);

        StringFormat ellipsisFormat = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        var visibleItems = _queue.Take(6).ToList();
        for (int i = 0; i < visibleItems.Count; i++)
        {
            var item = visibleItems[i];
            bool isHovered = (i == _hoveredQueueIndex);

            RectangleF textRect = new RectangleF(20, itemY, Width - 85, 20);
            string fullText = string.IsNullOrEmpty(item.Channel) ? item.Title : $"{item.Title} • {item.Channel}";

            g.DrawString(fullText, titleFont, titleBrush, textRect, ellipsisFormat);

            if (isHovered)
            {
                int crossX = Width - 48;
                int crossY = itemY + 3;
                g.DrawLine(deletePen, crossX, crossY, crossX + 10, crossY + 10);
                g.DrawLine(deletePen, crossX + 10, crossY, crossX, crossY + 10);
            }
            else
            {
                string durationStr = item.Duration.TotalSeconds > 0 ? item.Duration.ToString(@"m\:ss") : "--:--";
                g.DrawString(durationStr, durationFont, durationBrush, Width - 55, itemY);
            }

            itemY += 26;
        }
    }

    private void DrawMusicNote(Graphics g, Point center)
    {
        using SolidBrush b = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
        using Pen p = new Pen(Color.FromArgb(230, 255, 255, 255), 3);
        g.FillEllipse(b, center.X - 18, center.Y + 6, 12, 8);
        g.DrawLine(p, center.X - 8, center.Y + 10, center.X - 8, center.Y - 14);
        g.DrawLine(p, center.X - 8, center.Y - 14, center.X + 8, center.Y - 20);
    }

    private void DrawPlayIcon(Graphics g, Rectangle r, Color color)
    {
        using SolidBrush b = new SolidBrush(color);
        PointF[] p = {
            new PointF(r.X + r.Width * 0.40f, r.Y + r.Height * 0.28f),
            new PointF(r.X + r.Width * 0.40f, r.Y + r.Height * 0.72f),
            new PointF(r.X + r.Width * 0.74f, r.Y + r.Height * 0.5f)
        };
        g.FillPolygon(b, p);
    }

    private void DrawPauseIcon(Graphics g, Rectangle r, Color color)
    {
        using SolidBrush b = new SolidBrush(color);
        float barW = r.Width * 0.11f;
        float barH = r.Height * 0.4f;
        g.FillRectangle(b, r.X + r.Width * 0.34f, r.Y + (r.Height - barH) / 2, barW, barH);
        g.FillRectangle(b, r.X + r.Width * 0.55f, r.Y + (r.Height - barH) / 2, barW, barH);
    }

    private void DrawNextTrackIcon(Graphics g, Rectangle r, Color color)
    {
        using SolidBrush b = new SolidBrush(color);
        using Pen p = new Pen(color, 2f);

        PointF[] arrow = {
            new PointF(r.X + r.Width * 0.35f, r.Y + r.Height * 0.30f),
            new PointF(r.X + r.Width * 0.35f, r.Y + r.Height * 0.70f),
            new PointF(r.X + r.Width * 0.62f, r.Y + r.Height * 0.50f)
        };
        g.FillPolygon(b, arrow);

        g.DrawLine(p, r.X + r.Width * 0.67f, r.Y + r.Height * 0.30f, r.X + r.Width * 0.67f, r.Y + r.Height * 0.70f);
    }

    private void DrawCircularArrow(Graphics g, Rectangle r, Color color, bool isForward)
    {
        int margin = 8;
        Rectangle arcRect = new Rectangle(r.X + margin, r.Y + margin, r.Width - margin * 2, r.Height - margin * 2);

        if (arcRect.Width <= 0 || arcRect.Height <= 0) return;

        using Pen p = new Pen(color, 2.0f);
        using CustomLineCap arrowCap = new AdjustableArrowCap(3.2f, 3.6f, true);
        p.CustomEndCap = arrowCap;

        if (isForward)
        {
            g.DrawArc(p, arcRect, 30, 290);
        }
        else
        {
            g.DrawArc(p, arcRect, 150, -290);
        }
    }

    private void DrawSpeakerIcon(Graphics g, Point pt, Color color, bool isHigh)
    {
        using SolidBrush b = new SolidBrush(color);
        using Pen p = new Pen(color, 2);
        g.FillRectangle(b, pt.X, pt.Y + 4, 3, 6);
        PointF[] pPolygon = { new PointF(pt.X + 3, pt.Y + 4), new PointF(pt.X + 7, pt.Y + 1), new PointF(pt.X + 7, pt.Y + 13), new PointF(pt.X + 3, pt.Y + 10) };
        g.FillPolygon(b, pPolygon);

        if (isHigh)
        {
            g.DrawArc(p, pt.X + 7, pt.Y + 2, 6, 10, -60, 120);
            g.DrawArc(p, pt.X + 9, pt.Y, 9, 14, -60, 120);
        }
        else
        {
            g.DrawArc(p, pt.X + 7, pt.Y + 3, 5, 8, -60, 120);
        }
    }

    private GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int diameter = radius * 2;

        if (rect.Width < diameter || rect.Height < diameter)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private GraphicsPath GetTopRoundedRectPath(Rectangle rect, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int diameter = radius * 2;

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddLine(rect.Right, rect.Y + radius, rect.Right, rect.Bottom);
        path.AddLine(rect.Right, rect.Bottom, rect.X, rect.Bottom);
        path.CloseFigure();
        return path;
    }
}