// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System.ComponentModel;
using System.Windows.Forms;

namespace DuoAssistant
{
    partial class Home
    {
        private IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null) components.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>
        /// UI 全部由代码构建（Views/Home.cs + Views/Pages/*.cs）。
        /// 不使用设计器序列化，避免 .NET 10 下 resx 强类型资源反序列化问题。
        /// </summary>
        private void InitializeComponent()
        {
            components = new Container();
            SuspendLayout();
            BuildUi();
            ResumeLayout(false);
            PerformLayout();
        }
    }
}
