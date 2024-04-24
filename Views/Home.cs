using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DuoAssistant
{
    public partial class Home : Form
    {
        public Home()
        {
            InitializeComponent();
            this.SizeChanged += new EventHandler(MainForm_SizeChaned);
        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {

        }
        
        /// <summary>
        /// 窗口变化事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MainForm_SizeChaned(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                // 窗体已最小化，这里可以执行你的逻辑
                // 例如，可以隐藏窗体或者只把窗体隐藏到系统托盘区域
                this.Hide();
                // 或者
                // this.ShowInTaskbar = false;
            }
        }
    }
}
