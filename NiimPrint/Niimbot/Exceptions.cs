namespace Niimbot;

public sealed class BleException(string message) : Exception(message);

public sealed class PrinterException(string message) : Exception(message);
