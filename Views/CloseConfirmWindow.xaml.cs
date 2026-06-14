using System.Windows;

namespace rdpManager.Views
{
    public enum CloseConfirmResult
    {
        Cancel,
        Exit,
        Minimize
    }

    public partial class CloseConfirmWindow : Window
    {
        public CloseConfirmResult Result { get; private set; } = CloseConfirmResult.Cancel;

        public CloseConfirmWindow()
        {
            InitializeComponent();
            
            // 允许无边框窗口拖拽移动
            this.MouseLeftButtonDown += (s, e) =>
            {
                if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                {
                    this.DragMove();
                }
            };
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Result = CloseConfirmResult.Cancel;
            this.Close();
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Result = CloseConfirmResult.Exit;
            this.Close();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            Result = CloseConfirmResult.Minimize;
            this.Close();
        }
    }
}
