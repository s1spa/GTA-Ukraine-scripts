using System;
using System.Windows.Forms;
using ScriptedProgram.UI;

namespace ScriptedProgram;

class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "Thread Exception", MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            MessageBox.Show(e.ExceptionObject.ToString(), "Unhandled Exception", MessageBoxButtons.OK, MessageBoxIcon.Error);

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
