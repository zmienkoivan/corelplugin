using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace VanyaTools.Native
{
    internal sealed class DiagnosticTools
    {
        internal FrameworkElement SettingsView { get; }
        private readonly Func<Tuple<string,string>> _connection;
        private readonly Func<string> _token;
        private readonly Action<string,bool> _status;
        private readonly TextBox _note;
        private readonly TextBlock _info;
        private readonly Button _send,_save,_retry,_open;
        private SavedDiagnosticReport _last;
        private bool _busy;
        internal DiagnosticTools(Func<Tuple<string,string>> connection,Func<string> token,Action<string,bool> status)
        {
            _connection=connection;_token=token;_status=status;
            var panel=new StackPanel();panel.Children.Add(new TextBlock{Text="Что произошло · необязательно",FontSize=11});
            _note=new TextBox{AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=52,MaxLength=2000,Margin=new Thickness(0,3,0,5)};panel.Children.Add(_note);
            _send=Button("Сохранить и отправить лог",async()=>await SaveAsync(true));panel.Children.Add(_send);
            var row=new System.Windows.Controls.Primitives.UniformGrid{Columns=2};
            _save=Button("Только сохранить",async()=>await SaveAsync(false));row.Children.Add(_save);
            _open=Button("Папка отчётов",()=>{Directory.CreateDirectory(DiagnosticReport.Folder);Process.Start(new ProcessStartInfo(DiagnosticReport.Folder){UseShellExecute=true});return Task.CompletedTask;});row.Children.Add(_open);panel.Children.Add(row);
            _retry=Button("Повторить отправку",async()=>await SendLastAsync());_retry.Visibility=Visibility.Collapsed;panel.Children.Add(_retry);
            _info=new TextBlock{TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,4,0,0)};panel.Children.Add(_info);
            DockerHelp.Add(panel,"Отправляются последние записи журналов, версия плагина и Corel, Windows, процессор, имя ПК и сотрудника, описание проблемы. Макеты, фотографии и файлы настроек не включаются. Ключи и токены скрываются. Копия JSON остаётся в локальной папке Diagnostics; сервер — из настроек карточки заказа. Повтор отправляет тот же отчёт с тем же номером.");
            SettingsView=DockerSettings.Section("Диагностика",panel);
        }
        private Button Button(string text,Func<Task> run)
        {
            var button=new Button{Content=text,MinHeight=30,Margin=new Thickness(0,2,3,2),Padding=new Thickness(5,2,5,2)};
            button.Click+=async(_,__)=>{try{await run();}catch(Exception error){Show(error.Message,true);}};return button;
        }
        private void Busy(bool value){_busy=value;_send.IsEnabled=_save.IsEnabled=_retry.IsEnabled=_note.IsEnabled=!value;}
        private void Show(string message,bool error=false){_info.Text=message;_status(message,error);}
        private async Task SaveAsync(bool send)
        {
            if(_busy)return;Busy(true);bool saved=false;
            try
            {
                var connection=_connection();string employee=WorkTimeRuntime.Settings.Employee,note=_note.Text,corel=DiagnosticReport.CorelVersion();
                var secrets=new[]{connection.Item2,_token()};Show("Сохраняю диагностический отчёт…");
                _last=await Task.Run(()=>DiagnosticReport.Save(DiagnosticReport.Folder,employee,note,corel,secrets));
                saved=true;_retry.Visibility=Visibility.Visible;Show("Лог сохранён: "+_last.FilePath);
                if(send)await UploadAsync(connection);
            }
            catch(Exception error){Show("Лог "+(!saved?"не сохранён: ":"остаётся на ПК: "+_last.FilePath+"\n")+error.Message,true);}
            finally{Busy(false);}
        }
        private async Task SendLastAsync()
        {
            if(_busy || _last==null)return;Busy(true);
            try{await UploadAsync(_connection());}
            catch(Exception error){Show("Не отправлено. Копия: "+_last.FilePath+"\n"+error.Message,true);}
            finally{Busy(false);}
        }
        private async Task UploadAsync(Tuple<string,string> connection)
        {
            if(String.IsNullOrWhiteSpace(connection.Item1) || String.IsNullOrWhiteSpace(connection.Item2))throw new InvalidOperationException("Укажите адрес и ключ в разделе «Сервер заказов».");
            Show("Отправляю отчёт "+_last.Id+"…");
            var reply=await OrderServerClient.SendDiagnostics(connection.Item1,connection.Item2,_last.FilePath);
            if(!reply.TryGetValue("id",out object id) || Convert.ToString(id)!=_last.Id)throw new InvalidDataException("Сервер не подтвердил номер отчёта. Локальная копия сохранена.");
            _retry.Visibility=Visibility.Collapsed;Show("Отчёт отправлен · "+_last.Id+"\nКопия: "+_last.FilePath);
        }
    }
    internal sealed partial class OrderCardTab
    {
        internal Tuple<string,string> DiagnosticConnection()=>Tuple.Create(_server.Text.Trim(),_key.Password.Trim());
    }
}
