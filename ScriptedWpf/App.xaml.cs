using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ScriptedWpf;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException          += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowCrash(e.Exception);
        e.Handled = true;
    }

    void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        ShowCrash(ex, isTerminating: e.IsTerminating);
    }

    void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        Dispatcher.Invoke(() => ShowCrash(e.Exception));
    }

    static void ShowCrash(Exception? ex, bool isTerminating = false)
    {
        string msg = ex == null
            ? "Невідома помилка."
            : BuildMessage(ex);

        string title = isTerminating ? "Критична помилка (завершення)" : "Помилка";
        MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    static string BuildMessage(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(ex.GetType().Name + ": " + ex.Message);
        if (ex.StackTrace is { } st)
            sb.AppendLine(st);

        var inner = ex.InnerException;
        while (inner != null)
        {
            sb.AppendLine();
            sb.AppendLine("Причина: " + inner.GetType().Name + ": " + inner.Message);
            if (inner.StackTrace is { } ist)
                sb.AppendLine(ist);
            inner = inner.InnerException;
        }

        if (ex is AggregateException agg)
            foreach (var e in agg.InnerExceptions)
            {
                sb.AppendLine();
                sb.AppendLine("--- " + e.GetType().Name + ": " + e.Message);
                if (e.StackTrace is { } est) sb.AppendLine(est);
            }

        return sb.ToString();
    }
}

