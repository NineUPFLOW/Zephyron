using Zephyron.Core;
using Zephyron.Supervisor;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// Программа в облачной папке — оговорка «Диагностики».
/// </summary>
/// <remarks>
/// Отчёт друга владельца 06.10: <c>%USERPROFILE%\OneDrive\Рабочий стол\Zephyron</c>.
/// Владелец 07.10: «делай».
/// </remarks>
public sealed class CloudFolderTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void OneDriveIsFoundByItsVariable()
    {
        var env = Env(("OneDrive", @"C:\Users\friend\OneDrive"));

        Assert.Equal("OneDrive", CloudFolder.Of(@"C:\Users\friend\OneDrive\Рабочий стол\Zephyron", env));
        Assert.Equal("OneDrive", CloudFolder.Of(@"c:\users\friend\onedrive\zephyron\", env));
        Assert.Null(CloudFolder.Of(@"C:\Users\friend\OneDriveBackup\Zephyron", Env(("OneDrive", @"C:\Users\friend\OneDrive2"))));
    }

    [Fact]
    public void OneDriveIsFoundByTheFolderNameWithoutVariables()
    {
        Assert.Equal("OneDrive", CloudFolder.Of(@"D:\OneDrive\Zephyron", Env()));
        Assert.Equal("OneDrive", CloudFolder.Of(@"C:\Users\u\OneDrive - Контора\Desktop\Zephyron", Env()));
    }

    [Theory]
    [InlineData(@"C:\Users\u\Dropbox\Zephyron", "Dropbox")]
    [InlineData(@"G:\My Drive\Zephyron", "Google Диск")]
    [InlineData(@"C:\Users\u\YandexDisk\Zephyron", "Яндекс Диск")]
    public void OtherClientsAreFoundByFolder(string path, string cloud) =>
        Assert.Equal(cloud, CloudFolder.Of(path, Env()));

    [Fact]
    public void AnOrdinaryFolderIsNotCloud()
    {
        Assert.Null(CloudFolder.Of(@"C:\Zephyron", Env(("OneDrive", @"C:\Users\u\OneDrive"))));
        Assert.Null(CloudFolder.Of(@"C:\Users\u\Desktop\Zephyron", Env()));
    }

    [Fact]
    public void DoctorWarnsAndSaysWhatToDo()
    {
        var line = Assert.Single(Doctor.Place(@"C:\Users\u\OneDrive\Рабочий стол\Zephyron", Env(("OneDrive", @"C:\Users\u\OneDrive"))));

        Assert.Equal(DoctorLevel.Warn, line.Level);
        Assert.Contains("OneDrive", line.Text);
        Assert.Contains(@"C:\Zephyron", line.Text);
        Assert.Contains("автозапуск", line.Text);

        Assert.Equal(DoctorLevel.Ok, Assert.Single(Doctor.Place(@"C:\Zephyron", Env())).Level);
    }
}
