using System;
using System.Windows;

namespace rdpManager.Views
{
    public partial class LockPasswordDialog : Window
    {
        public string Password { get; private set; } = string.Empty;

        public LockPasswordDialog()
        {
            InitializeComponent();
            
            // 允许拖动窗口
            this.MouseLeftButtonDown += (s, e) =>
            {
                if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                {
                    this.DragMove();
                }
            };

            // 自动聚焦密码框
            this.Loaded += (s, e) =>
            {
                TxtPassword.Focus();
            };
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string pwd = TxtPassword.Password;
            if (string.IsNullOrEmpty(pwd))
            {
                TxtError.Text = "密码不能为空！";
                TxtError.Visibility = Visibility.Visible;
                return;
            }

            Password = pwd;
            this.DialogResult = true;
            this.Close();
        }
    }
}
