using MetroSuite;
using System.Diagnostics;
using System.Windows.Forms;
using System.IO;

public partial class MainForm : MetroForm
{
    public MainForm()
    {
        InitializeComponent();
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;
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

        foreach (string file in Directory.GetFiles("inputs"))
        {
            ImageCompressor.CompressImage(file, Path.GetFullPath("outputs") + "\\" + Path.GetFileName(file));
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
}