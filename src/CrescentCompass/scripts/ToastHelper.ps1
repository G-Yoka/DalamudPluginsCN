param(
    [Parameter(Mandatory = $true)][string] $TitleBase64,
    [Parameter(Mandatory = $true)][string] $ContentBase64,
    [Parameter(Mandatory = $true)][string] $AttributionBase64,
    [Parameter(Mandatory = $true)][string] $IconPng,
    [Parameter(Mandatory = $true)][string] $IconIco
)

$ErrorActionPreference = 'Stop'
$appId = 'G-Yoka.CrescentCompass'
$displayName = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('5paw5pyI572X55uY'))
$programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$shortcutPath = Join-Path $programs "$displayName.lnk"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace CrescentCompassToast
{
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLinkObject { }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant : IDisposable
    {
        [FieldOffset(0)] private ushort valueType;
        [FieldOffset(8)] private IntPtr pointerValue;

        public PropVariant(string value)
        {
            this = default(PropVariant);
            valueType = (ushort)VarEnum.VT_LPWSTR;
            pointerValue = Marshal.StringToCoTaskMemUni(value);
        }

        public void Dispose()
        {
            PropVariantClear(ref this);
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal interface IPropertyStore
    {
        uint GetCount();
        PropertyKey GetAt(uint propertyIndex);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    public static class ShortcutInstaller
    {
        public static void SetAppId(string shortcutPath, string appId)
        {
            object instance = new ShellLinkObject();
            try
            {
                IPersistFile persistFile = (IPersistFile)instance;
                persistFile.Load(shortcutPath, 2);
                IPropertyStore store = (IPropertyStore)instance;
                var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
                var value = new PropVariant(appId);
                try
                {
                    store.SetValue(ref key, ref value);
                    store.Commit();
                }
                finally
                {
                    value.Dispose();
                }

                persistFile.Save(shortcutPath, true);
            }
            finally
            {
                if (Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
            }
        }
    }
}
'@

$powershell = Join-Path $PSHOME 'powershell.exe'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $powershell
$shortcut.Arguments = '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -Command exit'
$shortcut.Description = 'CrescentCompass event notifications'
$shortcut.IconLocation = "$IconIco,0"
$shortcut.Save()
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
[GC]::Collect()
[GC]::WaitForPendingFinalizers()
Start-Sleep -Milliseconds 100
[CrescentCompassToast.ShortcutInstaller]::SetAppId($shortcutPath, $appId)
if (-not (Test-Path -LiteralPath $shortcutPath)) {
    throw "Unable to create the CrescentCompass notification shortcut: $shortcutPath"
}

$identityKey = "HKCU:\Software\Classes\AppUserModelId\$appId"
New-Item -Path $identityKey -Force | Out-Null
New-ItemProperty -Path $identityKey -Name 'DisplayName' -Value $displayName -PropertyType String -Force | Out-Null
New-ItemProperty -Path $identityKey -Name 'IconUri' -Value $IconIco -PropertyType String -Force | Out-Null

[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null

$title = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($TitleBase64))
$content = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ContentBase64))
$attribution = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($AttributionBase64))
$safeTitle = [Security.SecurityElement]::Escape($title)
$safeContent = [Security.SecurityElement]::Escape($content)
$safeAttribution = [Security.SecurityElement]::Escape($attribution)
$safeIcon = [Security.SecurityElement]::Escape(([Uri]::new($IconPng)).AbsoluteUri)
$xmlText = @"
<toast duration="short">
  <visual>
    <binding template="ToastGeneric">
      <image placement="appLogoOverride" hint-crop="circle" src="$safeIcon" />
      <text>$safeTitle</text>
      <text>$safeContent</text>
      <text placement="attribution">$safeAttribution</text>
    </binding>
  </visual>
</toast>
"@
$xml = [Windows.Data.Xml.Dom.XmlDocument]::new()
$xml.LoadXml($xmlText)
$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
$toast.ExpirationTime = [DateTimeOffset]::Now.AddSeconds(8)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)
