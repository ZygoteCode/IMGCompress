﻿using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

public class ImageCompressor
{
    private static ProtoRandom.ProtoRandom _random = new ProtoRandom.ProtoRandom(5);
    private static char[] _characters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();

    public static void CompressImage(string inputPath, string outputPath, bool supportPngTransparentImages)
    {
        string rootDir = Environment.GetFolderPath(Environment.SpecialFolder.System).Substring(0, 1) + ":";
        string tempFolder = rootDir + "\\Temp";

        if (!Directory.Exists(tempFolder))
        {
            Directory.CreateDirectory(tempFolder);
        }

        string image1Name = GenerateRandomFileName();
        RunExecutable("ffmpeg.exe",
            $"-threads {Environment.ProcessorCount} -i \"{inputPath}\" -pred mixed \"{tempFolder + "\\" + image1Name + ".png"}\"");
        RunExecutable("pngquant.exe",
            $"\"{tempFolder + "\\" + image1Name + ".png"}\" --force --verbose --ordered --speed=1 --quality=50-90 %1");

        if (!(Path.GetExtension(inputPath).ToLower().Equals(".png") && IsTransparentPngImage(inputPath) && supportPngTransparentImages))
        {
            string outputExtension = Path.GetExtension(outputPath);

            if (outputExtension != ".avif")
            {
                outputPath = outputPath.Substring(0, outputPath.Length - outputExtension.Length);
                outputPath += ".avif";
            }    

            string image2Name = GenerateRandomFileName();

            RunExecutable("ffmpeg.exe",
                $"-threads {Environment.ProcessorCount} -i \"{tempFolder + "\\" + image1Name + "-or8.png"}\" -pred mixed \"{tempFolder + "\\" + image2Name + ".jpg"}\"");
          
            string image3Name = GenerateRandomFileName();
            ImageCodecInfo encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            EncoderParameters encoderParameters = new EncoderParameters(1);
            encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
            Image image = Image.FromFile(tempFolder + "\\" + image2Name + ".jpg");
            image.Save(tempFolder + "\\" + image3Name + ".jpg", encoder, encoderParameters);
            image.Dispose();

            string image4Name = GenerateRandomFileName();
            RunExecutable("ffmpeg.exe",
                $"-threads {Environment.ProcessorCount} -i \"{tempFolder + "\\" + image3Name + ".jpg"}\" -pred mixed \"{tempFolder + "\\" + image4Name + ".jpg"}\"");

            string image5Name = GenerateRandomFileName();
            RunExecutable("ffmpeg.exe",
                $"-threads {Environment.ProcessorCount} -i \"{tempFolder + "\\" + image4Name + ".jpg"}\" \"{tempFolder + "\\" + image5Name + ".webp"}\"");

            RunExecutable("ffmpeg.exe",
                $"-threads {Environment.ProcessorCount} -i \"{tempFolder + "\\" + image5Name + ".webp"}\" \"{outputPath}\"");

            TryDeleteFile(tempFolder + "\\" + image1Name + ".png");
            TryDeleteFile(tempFolder + "\\" + image1Name + "-or8.png");
            TryDeleteFile(tempFolder + "\\" + image2Name + ".jpg");
            TryDeleteFile(tempFolder + "\\" + image3Name + ".jpg");
            TryDeleteFile(tempFolder + "\\" + image4Name + ".jpg");
            TryDeleteFile(tempFolder + "\\" + image5Name + ".webp");
        }
        else
        {
            string image2Name = GenerateRandomFileName(),
                image3Name = GenerateRandomFileName();

            RunExecutable("ffmpeg.exe",
                $"-threads {Environment.ProcessorCount} -i \"{tempFolder + "\\" + image1Name + "-or8.png"}\" -pred mixed \"{tempFolder + "\\" + image2Name + ".png"}\"");

            RunExecutable("ffmpeg.exe",
               $"-threads {Environment.ProcessorCount} -i \"{tempFolder + "\\" + image2Name + ".png"}\" \"{tempFolder + "\\" + image3Name + ".webp"}\"");

            File.Move(tempFolder + "\\" + image3Name + ".webp", outputPath);

            TryDeleteFile(tempFolder + "\\" + image1Name + ".png");
            TryDeleteFile(tempFolder + "\\" + image1Name + "-or8.png");
            TryDeleteFile(tempFolder + "\\" + image2Name + ".png");
            TryDeleteFile(tempFolder + "\\" + image3Name + ".webp");
        }
    }

    public static bool IsTransparentPngImage(string inputImageFilePath)
    {
        Bitmap image = (Bitmap) Image.FromFile(inputImageFilePath);
        
        for (int i = 0; i < image.Width; i++)
        {
            for (int j = 0; j < image.Height; j++)
            {
                if (image.GetPixel(i, j).A != 255)
                {
                    image.Dispose();
                    return true;
                }
            }
        }

        return false;
    }

    private static string GenerateRandomFileName()
    {
        return _random.GetRandomString(_characters, 28);
    }

    private static void RunExecutable(string executableName, string arguments)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = executableName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }).WaitForExit();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {

        }
    }
}