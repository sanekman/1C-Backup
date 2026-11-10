using System.IO;
using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyOneCCredentialsAsync(MainWindow window,Action<string> screenshot)
    {
        T Control<T>(string name) where T:FrameworkElement=>(T)window.FindName(name);
        void Click(string name) { var button=Control<Button>(name);button.BringIntoView();window.UpdateLayout();button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout(); }
        void Require(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
        var root=Path.Combine(Path.GetDirectoryName(DesktopPaths.DataDirectory)!,"onec-credential-ui");Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"1Cv8.1CD"),"UI fixture; not a real 1C database");
        File.WriteAllText(Path.Combine(root,"1cv8.exe"),"UI fixture; never execute");
        var store=new WindowsOneCCredentialStore();string? key=null;
        const string secret="owned-onec-ui-secret";
        try
        {
            Click("JobsNavigation");Click("NewJobButton");
            Control<TextBox>("JobName").Text="1С — пользователь ИБ";Control<TextBox>("Source").Text=root;
            Control<ComboBox>("OneCMethodPicker").SelectedValue=OneCBackupMethod.PlatformDump;
            Control<CheckBox>("AutoOneCPlatform").IsChecked=false;Control<TextBox>("OneCExecutable").Text=Path.Combine(root,"1cv8.exe");
            Require(Control<StackPanel>("OneCPanel").IsVisible,"Вход ИБ не показан для DT.");
            window.OneCCredentialsDialogOpened=dialog=>
            {
                key=dialog.CredentialKey;
                dialog.Dispatcher.BeginInvoke(()=>
                {
                    ((TextBox)dialog.FindName("Username")).Text="ib-admin";
                    ((PasswordBox)dialog.FindName("Password")).Password=secret;
                    ((Button)dialog.FindName("CredentialSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
            };
            Click("OneCCredentialsButton");
            Require(Control<TextBlock>("OneCCredentialStatus").Text.Contains("ib-admin"),"Вход ИБ не подтверждён после сохранения.");
            screenshot("onec-infobase-credentials");
            Click("NextStepButton");Control<TextBox>("Destination").Text=Path.Combine(root,"copies");
            Click("NextStepButton");Control<ComboBox>("Schedule").SelectedValue=ScheduleKind.Manual;Control<CheckBox>("Enabled").IsChecked=false;
            Click("NextStepButton");Click("SaveButton");
            await WaitUntil(()=>window.SmokeReady && Control<Border>("JobListCard").IsVisible,"Задание DT с данными входа не сохранилось.");
            var path=Path.Combine(DesktopPaths.DataDirectory,"jobs.json");
            var job=(await new JobStore(path).LoadAsync()).Single(j=>j.Name=="1С — пользователь ИБ");
            Require(key is not null && job.OneC?.CredentialKey==key && job.OneC.Username=="ib-admin","Привязка пользователя ИБ потеряна.");
            Require((await store.GetAsync(key!)).Password==secret,"Пароль ИБ отсутствует в Windows Vault.");
            Require(!(await File.ReadAllTextAsync(path+".protected.json")).Contains(secret),"Пароль ИБ попал в JSON.");
            Click("EditJobButton");Require(Control<TextBlock>("OneCCredentialStatus").Text.Contains("ib-admin"),"Вход ИБ потерян после открытия задания.");
            Control<TextBox>("Source").Text=Path.Combine(root,"different-base");
            Require(Control<TextBlock>("OneCCredentialStatus").Text.Contains("заново"),"Пароль прежней базы повторно использован для другого источника.");
            Click("NextStepButton");Require(Control<TextBlock>("WizardError").IsVisible,"Изменённая привязка ИБ сохранена без нового пароля.");
            Click("CancelEditorButton");
        }
        finally { window.OneCCredentialsDialogOpened=null;if(key is not null)store.Delete(key);Directory.Delete(root,true); }
    }
}
