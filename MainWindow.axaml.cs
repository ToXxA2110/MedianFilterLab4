using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace MedianFilterApp
{
    public partial class MainWindow : Window
    {
        private byte[]? _srcPixels;
        private byte[]? _dstPixels;
        private int _imgWidth;
        private int _imgHeight;
        private int _imgStride;

        private CancellationTokenSource? _cts;
        private readonly object _logLock = new();

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            lock (_logLock)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    TxtLog.Text += line + Environment.NewLine;
                    TxtLog.CaretIndex = TxtLog.Text?.Length ?? 0;
                });
            }
        }

        // Этап 1-2: Асинхронный читатель
        private async void BtnLoad_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выберите изображение",
                AllowMultiple = false,
                FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
            });

            if (files.Count == 0) return;
            string? path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            BtnLoad.IsEnabled = false;
            BtnStart.IsEnabled = false;
            BtnSave.IsEnabled = false;
            TxtStatus.Text = "Асинхронное чтение файла...";

            try
            {
                var sw = Stopwatch.StartNew();
                byte[] fileBytes = await ReadFileAsync(path, CancellationToken.None);

                var decoded = await Task.Run(() =>
                {
                    using var ms = new MemoryStream(fileBytes);
                    using var srcBmp = new Bitmap(ms);
                    int w = srcBmp.PixelSize.Width;
                    int h = srcBmp.PixelSize.Height;
                    int stride = w * 4;

                    using var wb = new WriteableBitmap(
                        new PixelSize(w, h),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Unpremul);

                    // Конвертируем любое входное изображение в Bgra8888 через рендер или копирование
                    using (var msConv = new MemoryStream())
                    {
                        srcBmp.Save(msConv);
                        msConv.Position = 0;
                        using var wbLoaded = WriteableBitmap.Decode(msConv);
                        using var fb = wbLoaded.Lock();
                        int realStride = fb.RowBytes;
                        byte[] pixels = new byte[h * realStride];
                        Marshal.Copy(fb.Address, pixels, 0, pixels.Length);
                        return (w, h, realStride, pixels);
                    }
                });

                _imgWidth = decoded.w;
                _imgHeight = decoded.h;
                _imgStride = decoded.realStride;
                _srcPixels = decoded.pixels;
                _dstPixels = null;

                ImgOriginal.Source = CreateBitmapFromBuffer(_srcPixels, _imgWidth, _imgHeight, _imgStride);
                ImgResult.Source = null;

                sw.Stop();
                TxtStatus.Text = $"Загружено: {_imgWidth}×{_imgHeight} ({sw.ElapsedMilliseconds} мс)";
                Log($"Файл загружен асинхронно: {Path.GetFileName(path)} ({_imgWidth}x{_imgHeight}), время={sw.ElapsedMilliseconds} мс.");
                BtnStart.IsEnabled = true;
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Ошибка: {ex.Message}";
                Log($"Ошибка чтения: {ex.Message}");
            }
            finally
            {
                BtnLoad.IsEnabled = true;
            }
        }

        // Этап 3-5: Планировщик + Пул потоков + Сборщик результатов
        private async void BtnStart_Click(object? sender, RoutedEventArgs e)
        {
            if (_srcPixels == null) return;

            var selectedItem = CmbThreads.SelectedItem as ComboBoxItem;
            int poolSize = int.Parse(selectedItem?.Content?.ToString() ?? "4");
            int stripCount = Math.Min(_imgHeight, Math.Max(poolSize * 4, 32));

            BtnLoad.IsEnabled = false;
            BtnStart.IsEnabled = false;
            BtnSave.IsEnabled = false;
            BtnCancel.IsEnabled = true;
            ProgressBar.Value = 0;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            byte[] resultBuffer = new byte[_srcPixels.Length];

            Log($"Старт обработки: пул={poolSize} потоков, фрагментов={stripCount}.");
            var sw = Stopwatch.StartNew();

            try
            {
                using var pipeline = new MedianFilterPipeline(poolSize);

                await Task.Run(() =>
                {
                    pipeline.ProcessImage(
                        _srcPixels,
                        resultBuffer,
                        _imgWidth,
                        _imgHeight,
                        _imgStride,
                        stripCount,
                        onProgress: (done, total) =>
                        {
                            double pct = (double)done / total * 100.0;
                            Dispatcher.UIThread.Post(() =>
                            {
                                ProgressBar.Value = pct;
                                TxtStatus.Text = $"Обработка: {done}/{total} полос ({pct:F0}%)";
                            });
                        },
                        token);
                }, token);

                sw.Stop();
                _dstPixels = resultBuffer;
                ImgResult.Source = CreateBitmapFromBuffer(_dstPixels, _imgWidth, _imgHeight, _imgStride);

                TxtStatus.Text = $"Готово за {sw.ElapsedMilliseconds} мс (потоков: {poolSize})";
                Log($"Обработка завершена успешно за {sw.ElapsedMilliseconds} мс.");
                BtnSave.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                _dstPixels = null;
                ProgressBar.Value = 0;
                TxtStatus.Text = "Обработка отменена пользователем.";
                Log($"Операция отменена через {sw.ElapsedMilliseconds} мс. Частичный результат отброшен.");
            }
            catch (Exception ex)
            {
                Log($"Ошибка: {ex.Message}");
            }
            finally
            {
                BtnLoad.IsEnabled = true;
                BtnStart.IsEnabled = true;
                BtnCancel.IsEnabled = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void BtnCancel_Click(object? sender, RoutedEventArgs e)
        {
            BtnCancel.IsEnabled = false;
            TxtStatus.Text = "Отмена обработки...";
            Log("Запрошена отмена пользователем...");
            _cts?.Cancel();
        }

        // Этап 6: Асинхронный писатель
        private async void BtnSave_Click(object? sender, RoutedEventArgs e)
        {
            if (_dstPixels == null) return;

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить результат",
                SuggestedFileName = "median_filtered.png",
                DefaultExtension = "png"
            });

            if (file == null) return;
            string? path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            BtnSave.IsEnabled = false;
            BtnCancel.IsEnabled = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                var sw = Stopwatch.StartNew();
                var bmp = CreateBitmapFromBuffer(_dstPixels, _imgWidth, _imgHeight, _imgStride);

                byte[] encodedBytes = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    using var ms = new MemoryStream();
                    bmp.Save(ms);
                    return ms.ToArray();
                }, token);

                await WriteFileAsync(path, encodedBytes, token);
                sw.Stop();

                TxtStatus.Text = $"Сохранено за {sw.ElapsedMilliseconds} мс";
                Log($"Результат сохранён асинхронно: {path} ({sw.ElapsedMilliseconds} мс).");
            }
            catch (OperationCanceledException)
            {
                Log("Сохранение отменено. Незавершённый файл удалён.");
            }
            finally
            {
                BtnSave.IsEnabled = true;
                BtnCancel.IsEnabled = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private static WriteableBitmap CreateBitmapFromBuffer(byte[] buffer, int width, int height, int stride)
        {
            var wb = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);

            using var fb = wb.Lock();
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(buffer, y * stride, fb.Address + y * fb.RowBytes, Math.Min(stride, fb.RowBytes));
            }
            return wb;
        }

        private static async Task<byte[]> ReadFileAsync(string path, CancellationToken ct)
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            byte[] buffer = new byte[fs.Length];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                ct.ThrowIfCancellationRequested();
                int read = await fs.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
                if (read == 0) break;
                totalRead += read;
            }
            return buffer;
        }

        private static async Task WriteFileAsync(string path, byte[] data, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string tempPath = path + ".tmp";
            try
            {
                await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    await fs.WriteAsync(data.AsMemory(), ct);
                    await fs.FlushAsync(ct);
                }
                ct.ThrowIfCancellationRequested();
                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
                throw;
            }
        }
    }

    public sealed class StripJob
    {
        public required byte[] SrcBuffer { get; init; }
        public required byte[] DstBuffer { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int Stride { get; init; }
        public required int StartY { get; init; }
        public required int EndY { get; init; }
        public required int TotalStrips { get; init; }
        public Action<int, int>? OnProgress { get; init; }
    }

    public sealed class MedianFilterPipeline : IDisposable
    {
        private readonly int _poolSize;
        private readonly Thread[] _workers;
        private readonly Queue<StripJob> _taskQueue = new();

        private readonly object _queueLock = new();
        private readonly object _resultLock = new();

        private readonly AutoResetEvent _jobAvailableEvent = new(false);
        private readonly ManualResetEventSlim _stopEvent = new(false);
        private readonly SemaphoreSlim _concurrencySemaphore;
        private CountdownEvent? _allStripsDoneEvent;

        private volatile bool _isCancelled;
        private int _completedStrips;

        public MedianFilterPipeline(int poolSize)
        {
            _poolSize = Math.Max(1, poolSize);
            _concurrencySemaphore = new SemaphoreSlim(_poolSize, _poolSize);
            _workers = new Thread[_poolSize];

            for (int i = 0; i < _poolSize; i++)
            {
                _workers[i] = new Thread(WorkerLoop)
                {
                    Name = $"MedianWorker-{i + 1}",
                    IsBackground = true
                };
                _workers[i].Start();
            }
        }

        public void ProcessImage(
            byte[] srcBuffer,
            byte[] dstBuffer,
            int width,
            int height,
            int stride,
            int stripCount,
            Action<int, int>? onProgress,
            CancellationToken token)
        {
            _isCancelled = false;
            _completedStrips = 0;
            _allStripsDoneEvent = new CountdownEvent(stripCount);

            using var reg = token.Register(() =>
            {
                _isCancelled = true;
                lock (_queueLock)
                {
                    _taskQueue.Clear();
                }
            });

            int rowsPerStrip = height / stripCount;
            int remainder = height % stripCount;
            int currentY = 0;

            for (int i = 0; i < stripCount; i++)
            {
                int h = rowsPerStrip + (i < remainder ? 1 : 0);
                var job = new StripJob
                {
                    SrcBuffer = srcBuffer,
                    DstBuffer = dstBuffer,
                    Width = width,
                    Height = height,
                    Stride = stride,
                    StartY = currentY,
                    EndY = currentY + h,
                    TotalStrips = stripCount,
                    OnProgress = onProgress
                };
                currentY += h;

                lock (_queueLock)
                {
                    _taskQueue.Enqueue(job);
                }
                _jobAvailableEvent.Set();
            }

            _allStripsDoneEvent.Wait(token);
            token.ThrowIfCancellationRequested();
        }

        private void WorkerLoop()
        {
            WaitHandle[] handles = { _jobAvailableEvent, _stopEvent.WaitHandle };

            while (true)
            {
                int index = WaitHandle.WaitAny(handles);
                if (index == 1 || _stopEvent.IsSet)
                    break;

                while (true)
                {
                    StripJob? job = null;
                    lock (_queueLock)
                    {
                        if (_taskQueue.Count > 0)
                        {
                            job = _taskQueue.Dequeue();
                            if (_taskQueue.Count > 0)
                                _jobAvailableEvent.Set();
                        }
                    }

                    if (job == null) break;

                    _concurrencySemaphore.Wait();
                    try
                    {
                        if (!_isCancelled)
                        {
                            byte[] stripResult = ApplyMedian3x3(
                                job.SrcBuffer, job.Width, job.Height,
                                job.Stride, job.StartY, job.EndY);

                            lock (_resultLock)
                            {
                                if (!_isCancelled)
                                {
                                    int offset = job.StartY * job.Stride;
                                    Buffer.BlockCopy(stripResult, 0, job.DstBuffer, offset, stripResult.Length);
                                    _completedStrips++;
                                    job.OnProgress?.Invoke(_completedStrips, job.TotalStrips);
                                }
                            }
                        }
                    }
                    finally
                    {
                        _concurrencySemaphore.Release();
                        try { _allStripsDoneEvent?.Signal(); } catch (ObjectDisposedException) { }
                    }
                }
            }
        }

        private static unsafe byte[] ApplyMedian3x3(
            byte[] src, int width, int height, int stride, int startY, int endY)
        {
            int stripHeight = endY - startY;
            byte[] result = new byte[stripHeight * stride];
            Span<byte> winB = stackalloc byte[9];
            Span<byte> winG = stackalloc byte[9];
            Span<byte> winR = stackalloc byte[9];

            fixed (byte* pSrc = src)
            fixed (byte* pDst = result)
            {
                for (int y = startY; y < endY; y++)
                {
                    byte* dstRow = pDst + (y - startY) * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = 0;
                        for (int ky = -1; ky <= 1; ky++)
                        {
                            int sy = Math.Clamp(y + ky, 0, height - 1);
                            byte* srcRow = pSrc + sy * stride;
                            for (int kx = -1; kx <= 1; kx++)
                            {
                                int sx = Math.Clamp(x + kx, 0, width - 1);
                                int pOff = sx * 4;
                                winB[idx] = srcRow[pOff];
                                winG[idx] = srcRow[pOff + 1];
                                winR[idx] = srcRow[pOff + 2];
                                idx++;
                            }
                        }

                        InsertionSort9(winB);
                        InsertionSort9(winG);
                        InsertionSort9(winR);

                        int dOff = x * 4;
                        dstRow[dOff] = winB[4];
                        dstRow[dOff + 1] = winG[4];
                        dstRow[dOff + 2] = winR[4];
                        dstRow[dOff + 3] = pSrc[y * stride + dOff + 3];
                    }
                }
            }
            return result;
        }

        private static void InsertionSort9(Span<byte> data)
        {
            for (int i = 1; i < 9; i++)
            {
                byte key = data[i];
                int j = i - 1;
                while (j >= 0 && data[j] > key)
                {
                    data[j + 1] = data[j];
                    j--;
                }
                data[j + 1] = key;
            }
        }

        public void Dispose()
        {
            _isCancelled = true;
            _stopEvent.Set();
            foreach (var worker in _workers)
            {
                worker.Join(1000);
            }
            _jobAvailableEvent.Dispose();
            _stopEvent.Dispose();
            _concurrencySemaphore.Dispose();
            _allStripsDoneEvent?.Dispose();
        }
    }
}