
using DuoAssistant.Services;
using System;
using System.Windows.Forms;

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
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Home));
            this.textBox1 = new System.Windows.Forms.TextBox();
            this.notifyIcon1 = new System.Windows.Forms.NotifyIcon(this.components);
            this.SuspendLayout();
            // 
            // notifyIcon1
            // 
            this.notifyIcon1.Icon = ((System.Drawing.Icon)(resources.GetObject("notifyIcon1.Icon")));
            this.notifyIcon1.Text = "卖多货";
            this.notifyIcon1.Visible = true;
            // 
            // Home
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 17F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(800, 450);
            this.Controls.Add(this.textBox1);
            this.ForeColor = System.Drawing.Color.Coral;
            this.Name = "Home";
            this.ShowInTaskbar = false;
            this.Text = "卖多货打印助手";
            this.ResumeLayout(false);
            this.PerformLayout();
            isShow = false;
            this.WindowState = FormWindowState.Minimized;
            this.Hide();
            BaseServices baseServices = new BaseServices();
            baseServices.StartWhenSystemStart();



            //打开websock服务器
            WebsockServerService serverService = new WebsockServerService();
            serverService.StartNewWebSocket();


        }

        #endregion

        private System.Windows.Forms.TextBox textBox1;
        private System.Windows.Forms.NotifyIcon notifyIcon1;

        private void notifyIcon1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            isShow = !isShow;
            if (isShow)
            {
                this.Show();
            } else
            {
                this.Hide();
            }
            this.notifyIcon1.Visible = !isShow;
        }

    }
}

