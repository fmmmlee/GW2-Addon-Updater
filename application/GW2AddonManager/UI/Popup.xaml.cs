﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace GW2AddonManager
{
    /// <summary>
    /// Interaction logic for Popup.xaml
    /// </summary>
    public partial class Popup : Window
    {
        Storyboard _hide, _show;

        public MessageBoxResult Result => (DataContext as PopupViewModel).Result;

        private (double, double) ComputeLeftTop()
        {
            var refw = Application.Current.MainWindow;

            double left = refw.Left + refw.Width / 2 - Width / 2;
            double top = refw.Top + refw.Height / 6;

            return (left, top);
        }

        public Popup(string content, string title = "Message Box", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
        {
            var vm = new PopupViewModel(content, title, buttons, image);
            DataContext = vm;
            Opacity = 0;
            InitializeComponent();

            _show = FindResource("ShowWindow") as Storyboard;
            _hide = FindResource("HideWindow") as Storyboard;
            _hide.Completed += (_, _) => Close();

            vm.RequestClose += (_, _) => _hide.Begin(this);
            Loaded += (_, _) => {
                (Left, Top) = ComputeLeftTop();
                _show.Begin(this);
            };
        }

        private void TitleBar_MouseHeld(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == sender && e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        public static MessageBoxResult Show(string content, string title = "Message Box", MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
        {
            var p = new Popup(content, title, buttons, image);
            _ = p.ShowDialog();

            return p.Result;
        }
    }
}
