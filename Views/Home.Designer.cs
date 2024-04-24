
using DuoAssistant.Services;
using System;
using System.Windows.Forms;
using DuoAssistant.Utils;
using log4net;
using log4net.Config;

namespace DuoAssistant
{
    partial class Home
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        private bool isShow = false;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
            
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            
            assistantStart();
            BaseServices baseServices = new BaseServices();
            baseServices.StartWhenSystemStart();
            //打开websock服务器
            WebsockServerService serverService = new WebsockServerService();
            serverService.StartNewWebSocket();
        }

        #endregion

        private System.Windows.Forms.TextBox textBox1;
        private System.Windows.Forms.NotifyIcon notifyIcon1;

        /**
         * 开始项目
         */
        private void assistantStart()
        {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Home));
            this.textBox1 = new System.Windows.Forms.TextBox();
            this.notifyIcon1 = new System.Windows.Forms.NotifyIcon(this.components);
            this.SuspendLayout();
            // 
            // notifyIcon1
            // 
            this.notifyIcon1.Icon = ((System.Drawing.Icon)(resources.GetObject("notifyIcon1.Icon")));
            this.notifyIcon1.Text = "TinyAssistant";
            this.notifyIcon1.Visible = true;
            this.notifyIcon1.MouseDoubleClick += new MouseEventHandler(notifyIcon1_MouseDoubleClick);

            // 
            // Home
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 17F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(560, 760);
            this.Controls.Add(this.textBox1);
            this.ForeColor = System.Drawing.Color.Coral;
            this.Name = "Home";
            this.ShowInTaskbar = false;
            this.Text = "Tinyextreme Print Assistant";
            this.ResumeLayout(false);
            this.PerformLayout();
            isShow = false;
            this.WindowState = FormWindowState.Minimized;
            this.Hide();
        }
        
        /// <summary>
        /// 托盘双击事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void notifyIcon1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            Tools.Logger("双击事件");
            isShow = !isShow;
            if (isShow)
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
                Tools.Logger("显示窗口");
            } else
            {
                this.WindowState = FormWindowState.Minimized;
                this.Hide();
            }
        }

    }
}

