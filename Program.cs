using System;
using System.Threading;
using System.Windows.Forms;

namespace Bop;

static class Program
{
    private static Mutex? mutex;

    [STAThread]
    static void Main()
    {
        mutex = new Mutex(true, "Bop-Audio-Player-Unique-Instance-Key", out bool isNewInstance);

        if (!isNewInstance)
        {
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new BopApplicationContext());
        }
        finally
        {
            if (mutex != null)
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }
    }
}