namespace WarDogsOverlay;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\WarDogsOverlay.SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("WarDogs Overlay is already running.", "WarDogs Overlay",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
