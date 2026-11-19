using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RestaurantOrders.Services;

/// <summary>Sends raw ESC/POS bytes through an installed Windows printer queue.</summary>
public static class WindowsRawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocumentInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocumentName = "RestaurantOrders ESC/POS";
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType = "RAW";
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter(string printerName, out IntPtr printerHandle, IntPtr defaults);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(IntPtr printerHandle, int level, [In] DocumentInfo documentInfo);

    [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndDocPrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartPagePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPagePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", EntryPoint = "WritePrinter", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrinter(IntPtr printerHandle, byte[] buffer, int bufferLength, out int bytesWritten);

    public static void Send(string printerName, byte[] data, int copies)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("USB ESC/POS εκτύπωση απαιτεί τοπικό Windows POS ή Windows print bridge.");
        if (string.IsNullOrWhiteSpace(printerName))
            throw new ArgumentException("Συμπλήρωσε το ακριβές όνομα εκτυπωτή όπως εμφανίζεται στις ρυθμίσεις Windows.", nameof(printerName));
        if (data.Length == 0)
            throw new ArgumentException("Δεν υπάρχουν δεδομένα ESC/POS για εκτύπωση.", nameof(data));

        if (!OpenPrinter(printerName.Trim(), out var handle, IntPtr.Zero))
            throw LastPrinterError($"Δεν άνοιξε η ουρά Windows «{printerName}»");

        var documentStarted = false;
        var pageStarted = false;
        var operationFailed = false;
        Exception? cleanupError = null;
        try
        {
            var document = new DocumentInfo();
            if (StartDocPrinter(handle, 1, document) == 0)
                throw LastPrinterError($"Δεν ξεκίνησε εργασία RAW στον «{printerName}»");
            documentStarted = true;

            if (!StartPagePrinter(handle))
                throw LastPrinterError($"Δεν ξεκίνησε σελίδα στον «{printerName}»");
            pageStarted = true;
            for (var copy = 0; copy < Math.Clamp(copies, 1, 5); copy++)
            {
                if (!WritePrinter(handle, data, data.Length, out var written))
                    throw LastPrinterError($"Απέτυχε η αποστολή RAW ESC/POS στον «{printerName}»");
                if (written != data.Length)
                    throw new IOException($"Ο εκτυπωτής «{printerName}» δέχτηκε μόνο {written} από {data.Length} bytes.");
            }
        }
        catch
        {
            operationFailed = true;
            throw;
        }
        finally
        {
            if (pageStarted && !EndPagePrinter(handle))
                cleanupError ??= LastPrinterError($"Δεν ολοκληρώθηκε η σελίδα στον «{printerName}»");
            if (documentStarted && !EndDocPrinter(handle))
                cleanupError ??= LastPrinterError($"Δεν ολοκληρώθηκε η εργασία στον «{printerName}»");
            ClosePrinter(handle);
            if (!operationFailed && cleanupError is not null) throw cleanupError;
        }
    }

    private static Win32Exception LastPrinterError(string operation) =>
        new(Marshal.GetLastWin32Error(), operation);
}
