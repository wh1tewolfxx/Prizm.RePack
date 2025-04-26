using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.IO.Compression;
using ShellProgressBar;
using SkiaSharp;

namespace Prizm.RePack
{
    internal class Program
    {
        static void RepackFile(FileInfo input, int quality, int height, ProgressBar progressBar)
        {
            if (!input.Exists)
            {
                Console.WriteLine($"Input file not found: {input.FullName}");
                return;
            }

            string outputName = $"(Repack) {input.Name}";
            string outputPath = Path.Combine(input.DirectoryName!, outputName);

            var output = new FileInfo(outputPath);

            if (output.Exists) output.Delete();

            using var inputZip = ZipFile.OpenRead(input.FullName);
            using var outputZip = ZipFile.Open(output.FullName, ZipArchiveMode.Create);

            outputZip.Comment = inputZip.Comment;

            var imageEntries = inputZip.Entries
                .Where(e => e.FullName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            e.FullName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                            e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            e.FullName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var skSamplingOptions = new SKSamplingOptions(SKFilterMode.Linear);

            var sub = progressBar.Spawn(imageEntries.Count, "Processing images...",
                new ProgressBarOptions
                {
                    ProgressCharacter = '─',
                    ProgressBarOnBottom = true,
                    CollapseWhenFinished = false,
                });

            foreach (var entry in imageEntries)
            {

                // Read original image
                using var inputStream = entry.Open();
                using var memory = new MemoryStream();
                inputStream.CopyTo(memory);
                memory.Seek(0, SeekOrigin.Begin);

                using var bitmap = SKBitmap.Decode(memory);
                if (bitmap == null)
                {
                    Console.WriteLine($"Failed to decode: {entry.FullName}");
                    continue;
                }

                var resizedBitmap = bitmap;

                if (bitmap.Height > height && height != -1)
                {
                    float scale = height / (float)bitmap.Height;
                    int newWidth = (int)(bitmap.Width * scale);
                    int newHeight = (int)(bitmap.Height * scale);
                    resizedBitmap = bitmap.Resize(new SKImageInfo(newWidth, newHeight), skSamplingOptions);
                }

                using var image = SKImage.FromBitmap(resizedBitmap);
                using var data = image.Encode(SKEncodedImageFormat.Webp, quality);
                if (data == null)
                {
                    Console.WriteLine($"Failed to encode WebP: {entry.FullName}");
                    continue;
                }

                var newEntryName = Path.ChangeExtension(entry.FullName, ".webp");
                var newEntry = outputZip.CreateEntry(newEntryName, CompressionLevel.Optimal);
                using var outputStream = newEntry.Open();
                data.SaveTo(outputStream);

                sub.Tick($"Processing {entry.Name}");
            }
            progressBar.Tick();
            input.Refresh();
            output.Refresh();

            var oldSize = FormatSize(input.Length);
            var newSize = FormatSize(output.Length);

            var compressionRatio = ((double)output.Length / input.Length * 100).ToString("F2");

            sub.Message = $"Completed Successfully - Old Size: {oldSize} - New Size: {newSize} - Compression Ratio: {compressionRatio}%";
        }

        static void RepackCbzSingleFile(FileInfo input, int quality, int height)
        {
            try
            {
                var options = new ProgressBarOptions
                {
                    ProgressCharacter = '─',
                    ProgressBarOnBottom = true,
                    CollapseWhenFinished = false,
                };

                using var pbar = new ProgressBar(1, "Processing...", options);

                RepackFile(input, quality, height, pbar);

                pbar.Message = $"Done";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"Trace: {ex.StackTrace}");
            }

        }

        static void RepackCbzBatchFile(DirectoryInfo input, int quality, int height)
        {
            try
            {
                if (!input.Exists)
                {
                    Console.WriteLine($"Input directory not found: {input.FullName}");
                    return;
                }


                var cbzFiles = input.GetFiles("*.cbz", SearchOption.TopDirectoryOnly);

                if (cbzFiles.Length == 0)
                {
                    Console.WriteLine($"No CBZ files found in: {input.FullName}");
                    return;
                }

                var options = new ProgressBarOptions
                {
                    ProgressCharacter = '─',
                    ProgressBarOnBottom = true,
                    CollapseWhenFinished = false,
                };

                using var pbar = new ProgressBar(cbzFiles.Length, "Processing...", options);

                Parallel.ForEach(cbzFiles, cbzFile =>
                {
                    RepackFile(cbzFile, quality, height, pbar);
                });

                pbar.Message = $"Completed Successfully";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"Trace: {ex.StackTrace}");
            }

        }

        static string FormatSize(long bytes)
        {
            string[] sizes = ["B", "KB", "MB", "GB", "TB"];
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        static void Main(string[] args)
        {
            var rootCommand = new RootCommand("CBZ Repacker - Converts CBZ archive images to WebP.");

            var singleCommand = new Command("single", "Repack a CBZ file.")
            {
                new Option<FileInfo>(
                    ["--input", "-i"],
                    description: "Path to the input CBZ file.")
                    { IsRequired = true },
                new Option<int>(
                    ["--quality", "-q"],
                    description: "WebP compression quality (0-100).",
                    getDefaultValue: () => 75),
                new Option<int>(
                    ["--height", "-h"],
                    description: "Rescale image to max height keeping aspect ratio.",
                    getDefaultValue: () => -1)
            };


            var batchCommand = new Command("batch", "Repack CBZ files in the specified directory.")
            {
                new Option<DirectoryInfo>(
                    ["--input", "-i"],
                    description: "Path to the folder with CBZ files.")
                    { IsRequired = true },
                new Option<int>(
                    ["--quality", "-q"],
                    description: "WebP compression quality (0-100)",
                    getDefaultValue: () => 75),
                new Option<int>(
                    ["--height", "-h"],
                    description: "Rescale image to max height keeping aspect ratio.",
                    getDefaultValue: () => -1)
            };

            singleCommand.Handler = CommandHandler.Create<FileInfo, int, int>(RepackCbzSingleFile);
            batchCommand.Handler = CommandHandler.Create<DirectoryInfo, int, int>(RepackCbzBatchFile);

            rootCommand.AddCommand(singleCommand);
            rootCommand.AddCommand(batchCommand);

            rootCommand.InvokeAsync(args);

        }
    }


}




