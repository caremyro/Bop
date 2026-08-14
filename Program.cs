using System;
using System.Windows.Forms;

namespace Bop;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new BopApplicationContext());
    }
}