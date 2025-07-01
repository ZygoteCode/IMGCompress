﻿using MetroSuite;
using System.Diagnostics;
using System.Windows.Forms;
using System.IO;
using System.Threading;

public partial class MainForm : MetroForm
{
    public MainForm()
    {
        InitializeComponent();
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;
        CheckFolders();
        CheckForIllegalCrossThreadCalls = false;
        guna2ComboBox1.SelectedIndex = 2;
    }

    private void guna2Button1_Click(object sender, System.EventArgs e)
    {
        CheckFolders();

        if (Directory.GetFiles("inputs").Length == 0)
        {
            ShowError("Please, insert some valid image files into the 'inputs' folder.");
            return;
        }

        foreach (string file in Directory.GetFiles("outputs"))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                ShowError($"Failed to delete the file '{Path.GetFileName(file)}' in the 'outputs' folder. Maybe it's in use by another process.");
                return;
            }
        }

        if (!guna2CheckBox2.Checked)
        {
            int index = guna2ComboBox1.SelectedIndex;
            bool isChecked = guna2CheckBox1.Checked;
            string path = Path.GetFullPath("outputs") + "\\";

            foreach (string file in Directory.GetFiles("inputs"))
            {
                try
                {
                    switch (index)
                    {
                        case 0:
                            ImageCompressor.CompressImageLevel1(file,
                                path + Path.GetFileName(file),
                                isChecked);
                            break;
                        case 1:
                            ImageCompressor.CompressImageLevel2(file,
                                path + Path.GetFileName(file),
                                isChecked);
                            break;
                        case 2:
                            ImageCompressor.CompressImageLevel3(file,
                                path + Path.GetFileName(file),
                                isChecked);
                            break;
                    }
                }
                catch
                {

                }
            }
        }
        else
        {
            string[] files = Directory.GetFiles("inputs");
            int filesCount = files.Length, completed = 0;
            int index = guna2ComboBox1.SelectedIndex;
            bool isChecked = guna2CheckBox1.Checked;
            string path = Path.GetFullPath("outputs") + "\\";

            foreach (string file in files)
            {
                new Thread(() =>
                {
                    try
                    {
                        switch (index)
                        {
                            case 0:
                                ImageCompressor.CompressImageLevel1(file,
                                    path + Path.GetFileName(file),
                                    isChecked);
                                break;
                            case 1:
                                ImageCompressor.CompressImageLevel2(file,
                                    path + Path.GetFileName(file),
                                    isChecked);
                                break;
                            case 2:
                                ImageCompressor.CompressImageLevel3(file,
                                    path + Path.GetFileName(file),
                                    isChecked);
                                break;
                        }
                    }
                    finally
                    {
                        completed++;
                    }
                }).Start();
            }

            while (filesCount != completed)
            {
                Thread.Sleep(1);
            }
        }

        MessageBox.Show("Compression process has been completed successfully!", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public void ShowError(string message)
    {
        MessageBox.Show(message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    public void CheckFolders()
    {
        if (!Directory.Exists("inputs"))
        {
            Directory.CreateDirectory("inputs");
        }

        if (!Directory.Exists("outputs"))
        {
            Directory.CreateDirectory("outputs");
        }
    }

    private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        Process.Start("https://github.com/ZygoteCode/IMGCompress/");
    }

    private void guna2Button2_Click(object sender, System.EventArgs e)
    {
        CheckFolders();
        Process.Start(Path.GetFullPath("inputs"));
    }

    private void guna2Button3_Click(object sender, System.EventArgs e)
    {
        CheckFolders();
        Process.Start(Path.GetFullPath("outputs"));
    }
}
