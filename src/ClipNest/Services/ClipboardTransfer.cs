using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Models;

namespace ClipNest.Services;

public static class ClipboardTransfer
{
    public const string SelectionFormat = "ClipNest.Selection";
    private const string PngFormat = "PNG";
    private const int BitmapFileHeaderSize = 14;
    private const int BitmapInfoHeaderSize = 40;
    private const int BitCountOffset = 14;
    private const int CompressionOffset = 16;
    private const int ColorsUsedOffset = 32;
    private const int MaximumIndexedColorBits = 8;
    private const int PaletteEntryBytes = 4;
    private const int BitfieldMaskBytes = 12;
    private const int AlphaBitfieldMaskBytes = 16;
    private const uint BitfieldsCompression = 3;
    private const uint AlphaBitfieldsCompression = 6;
    private const ushort BitmapSignature = 0x4D42;
    private const int BytesPerPixel = 4;
    private const int OpaqueAlpha = 255;
    private const int Dpi = 96;

    public static BitmapSource? ReadImage()
    {
        IDataObject? data = Clipboard.GetDataObject();
        BitmapSource? image = TryReadPng(data) ?? TryReadDib(data);

        if (image != null)
        {
            return image;
        }

        image = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;

        if (image == null)
        {
            return null;
        }

        BitmapSource normalized = RepairMissingAlpha(image);
        normalized.Freeze();
        return normalized;
    }

    public static DataObject CreateData(IReadOnlyList<ClipboardEntry> entries, ClipboardHistory history)
    {
        history.RetainForTransfer(entries);
        DataObject data = new DataObject();
        data.SetData(SelectionFormat, "ClipNest");
        string[] files = entries.Where(entry => entry.IsText == false).Select(entry => entry.FilePath).ToArray();

        if (files.Length > 0)
        {
            string? missingPath = files.FirstOrDefault(path => File.Exists(path) == false && Directory.Exists(path) == false);

            if (missingPath != null)
            {
                throw new FileNotFoundException("Файл был перемещён или удалён.", missingPath);
            }

            data.SetData(DataFormats.FileDrop, files);
        }

        string[] texts = entries.Where(entry => entry.IsText).Select(entry => entry.Text).ToArray();

        if (texts.Length > 0)
        {
            data.SetText(string.Join(Environment.NewLine, texts), TextDataFormat.UnicodeText);
        }

        if (entries.Count == 1 && entries[0].Kind == ClipboardEntryKind.Image)
        {
            AddImageFormats(data, entries[0].FilePath);
        }

        return data;
    }

    private static BitmapSource? TryReadPng(IDataObject? data)
    {
        try
        {
            object? value = data?.GetDataPresent(PngFormat, false) == true ? data.GetData(PngFormat, false) : null;

            if (value is Stream stream)
            {
                return DecodePng(stream);
            }

            if (value is byte[] bytes)
            {
                using MemoryStream buffer = new MemoryStream(bytes);
                return DecodePng(buffer);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException or FileFormatException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static BitmapSource DecodePng(Stream stream)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource image = decoder.Frames[0];
        image.Freeze();
        return image;
    }

    private static BitmapSource? TryReadDib(IDataObject? data)
    {
        try
        {
            if (data?.GetDataPresent(DataFormats.Dib, false) != true || data.GetData(DataFormats.Dib, false) is not Stream stream)
            {
                return null;
            }

            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            using MemoryStream contents = new MemoryStream();
            stream.CopyTo(contents);
            byte[] dib = contents.ToArray();

            if (dib.Length < BitmapInfoHeaderSize)
            {
                return null;
            }

            int headerSize = BitConverter.ToInt32(dib, 0);
            ushort bitsPerPixel = BitConverter.ToUInt16(dib, BitCountOffset);
            uint compression = BitConverter.ToUInt32(dib, CompressionOffset);
            uint colorsUsed = BitConverter.ToUInt32(dib, ColorsUsedOffset);
            int maskBytes = headerSize == BitmapInfoHeaderSize ? compression == BitfieldsCompression ? BitfieldMaskBytes : compression == AlphaBitfieldsCompression ? AlphaBitfieldMaskBytes : 0 : 0;
            int paletteEntries = colorsUsed == 0 && bitsPerPixel <= MaximumIndexedColorBits ? 1 << bitsPerPixel : checked((int)colorsUsed);
            int pixelOffset = checked(BitmapFileHeaderSize + headerSize + maskBytes + paletteEntries * PaletteEntryBytes);

            if (headerSize < BitmapInfoHeaderSize || pixelOffset >= dib.Length + BitmapFileHeaderSize)
            {
                return null;
            }

            using MemoryStream bitmap = new MemoryStream();
            using BinaryWriter writer = new BinaryWriter(bitmap, System.Text.Encoding.UTF8, true);
            writer.Write(BitmapSignature);
            writer.Write(checked(dib.Length + BitmapFileHeaderSize));
            writer.Write(0);
            writer.Write(pixelOffset);
            writer.Write(dib);
            writer.Flush();
            bitmap.Position = 0;
            BitmapDecoder decoder = BitmapDecoder.Create(bitmap, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource image = RepairMissingAlpha(decoder.Frames[0]);
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException or OverflowException or FileFormatException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static BitmapSource RepairMissingAlpha(BitmapSource image)
    {
        if (image.Format != PixelFormats.Bgra32 && image.Format != PixelFormats.Pbgra32)
        {
            return image;
        }

        int stride = checked(image.PixelWidth * BytesPerPixel);
        byte[] pixels = new byte[checked(stride * image.PixelHeight)];
        image.CopyPixels(pixels, stride, 0);
        bool hasAlpha = false;

        for (int index = BytesPerPixel - 1; index < pixels.Length; index += BytesPerPixel)
        {
            if (pixels[index] != 0)
            {
                hasAlpha = true;
                break;
            }
        }

        if (hasAlpha)
        {
            return image;
        }

        for (int index = BytesPerPixel - 1; index < pixels.Length; index += BytesPerPixel)
        {
            pixels[index] = OpaqueAlpha;
        }

        return BitmapSource.Create(image.PixelWidth, image.PixelHeight, Dpi, Dpi, PixelFormats.Bgra32, null, pixels, stride);
    }

    private static void AddImageFormats(DataObject data, string path)
    {
        using FileStream input = File.OpenRead(path);
        BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource image = decoder.Frames[0];
        PngBitmapEncoder pngEncoder = new PngBitmapEncoder();
        pngEncoder.Frames.Add(BitmapFrame.Create(image));
        MemoryStream png = new MemoryStream();
        pngEncoder.Save(png);
        png.Position = 0;
        data.SetData(PngFormat, png, false);
        DrawingVisual visual = new DrawingVisual();
        Rect imageBounds = new Rect(0, 0, image.PixelWidth, image.PixelHeight);

        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, imageBounds);
            context.DrawImage(image, imageBounds);
        }

        RenderTargetBitmap opaque = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, Dpi, Dpi, PixelFormats.Pbgra32);
        opaque.Render(visual);
        FormatConvertedBitmap compatible = new FormatConvertedBitmap(opaque, PixelFormats.Bgr24, null, 0);
        BmpBitmapEncoder bitmapEncoder = new BmpBitmapEncoder();
        bitmapEncoder.Frames.Add(BitmapFrame.Create(compatible));
        using MemoryStream bitmapFile = new MemoryStream();
        bitmapEncoder.Save(bitmapFile);
        byte[] bytes = bitmapFile.ToArray();
        byte[] dib = new byte[bytes.Length - BitmapFileHeaderSize];
        Buffer.BlockCopy(bytes, BitmapFileHeaderSize, dib, 0, dib.Length);
        data.SetData(DataFormats.Dib, new MemoryStream(dib), false);
    }
}
