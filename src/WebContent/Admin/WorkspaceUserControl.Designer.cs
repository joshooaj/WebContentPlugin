namespace WebContent.Admin
{
    partial class WorkspaceUserControl
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        private void InitializeComponent()
        {
            this.lblName = new System.Windows.Forms.Label();
            this.txtName = new System.Windows.Forms.TextBox();
            this.lblHomeAddress = new System.Windows.Forms.Label();
            this.txtHomeAddress = new System.Windows.Forms.TextBox();
            this.chkRememberLastAddress = new System.Windows.Forms.CheckBox();
            this.chkHideToolbar = new System.Windows.Forms.CheckBox();
            this.chkHideAddress = new System.Windows.Forms.CheckBox();
            this.lblZoomLevel = new System.Windows.Forms.Label();
            this.txtZoomLevel = new System.Windows.Forms.TextBox();
            this.chkClearBrowsingDataOnExit = new System.Windows.Forms.CheckBox();
            this.chkAllowBackgroundBrowser = new System.Windows.Forms.CheckBox();
            this.chkAllowAudioNotifications = new System.Windows.Forms.CheckBox();
            this.chkMuteAudio = new System.Windows.Forms.CheckBox();
            this.chkIsolateBrowserInstances = new System.Windows.Forms.CheckBox();
            this.chkEnableDevTools = new System.Windows.Forms.CheckBox();
            this.lblSectionDisplay = new System.Windows.Forms.Label();
            this.lblSectionBehavior = new System.Windows.Forms.Label();
            this.lblSectionPrivacy = new System.Windows.Forms.Label();
            this.lblSectionAdvanced = new System.Windows.Forms.Label();
            this.SuspendLayout();
            // 
            // lblName
            // 
            this.lblName.AutoSize = true;
            this.lblName.Location = new System.Drawing.Point(16, 20);
            this.lblName.Name = "lblName";
            this.lblName.Size = new System.Drawing.Size(38, 13);
            this.lblName.TabIndex = 0;
            this.lblName.Text = "&Name:";
            // 
            // txtName
            // 
            this.txtName.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtName.Location = new System.Drawing.Point(160, 17);
            this.txtName.Name = "txtName";
            this.txtName.Size = new System.Drawing.Size(380, 20);
            this.txtName.TabIndex = 1;
            this.txtName.Text = "New Workspace";
            this.txtName.TextChanged += new System.EventHandler(this.OnUserChange);
            // 
            // lblHomeAddress
            // 
            this.lblHomeAddress.AutoSize = true;
            this.lblHomeAddress.Location = new System.Drawing.Point(16, 50);
            this.lblHomeAddress.Name = "lblHomeAddress";
            this.lblHomeAddress.Size = new System.Drawing.Size(84, 13);
            this.lblHomeAddress.TabIndex = 2;
            this.lblHomeAddress.Text = "Home &Address:";
            // 
            // txtHomeAddress
            // 
            this.txtHomeAddress.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtHomeAddress.Location = new System.Drawing.Point(160, 47);
            this.txtHomeAddress.Name = "txtHomeAddress";
            this.txtHomeAddress.Size = new System.Drawing.Size(380, 20);
            this.txtHomeAddress.TabIndex = 3;
            this.txtHomeAddress.TextChanged += new System.EventHandler(this.OnUserChange);
            // 
            // lblSectionDisplay
            // 
            this.lblSectionDisplay.AutoSize = true;
            this.lblSectionDisplay.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblSectionDisplay.Location = new System.Drawing.Point(16, 90);
            this.lblSectionDisplay.Name = "lblSectionDisplay";
            this.lblSectionDisplay.Size = new System.Drawing.Size(50, 13);
            this.lblSectionDisplay.TabIndex = 4;
            this.lblSectionDisplay.Text = "Display";
            // 
            // chkHideToolbar
            // 
            this.chkHideToolbar.AutoSize = true;
            this.chkHideToolbar.Location = new System.Drawing.Point(160, 90);
            this.chkHideToolbar.Name = "chkHideToolbar";
            this.chkHideToolbar.Size = new System.Drawing.Size(90, 17);
            this.chkHideToolbar.TabIndex = 5;
            this.chkHideToolbar.Text = "Hide &toolbar";
            this.chkHideToolbar.UseVisualStyleBackColor = true;
            this.chkHideToolbar.CheckedChanged += new System.EventHandler(this.chkHideToolbar_CheckedChanged);
            // 
            // chkHideAddress
            // 
            this.chkHideAddress.AutoSize = true;
            this.chkHideAddress.Location = new System.Drawing.Point(160, 113);
            this.chkHideAddress.Name = "chkHideAddress";
            this.chkHideAddress.Size = new System.Drawing.Size(94, 17);
            this.chkHideAddress.TabIndex = 6;
            this.chkHideAddress.Text = "Hide &address";
            this.chkHideAddress.UseVisualStyleBackColor = true;
            this.chkHideAddress.CheckedChanged += new System.EventHandler(this.OnUserChange);
            // 
            // lblZoomLevel
            // 
            this.lblZoomLevel.AutoSize = true;
            this.lblZoomLevel.Location = new System.Drawing.Point(16, 140);
            this.lblZoomLevel.Name = "lblZoomLevel";
            this.lblZoomLevel.Size = new System.Drawing.Size(64, 13);
            this.lblZoomLevel.TabIndex = 7;
            this.lblZoomLevel.Text = "&Zoom level:";
            // 
            // txtZoomLevel
            // 
            this.txtZoomLevel.Location = new System.Drawing.Point(160, 137);
            this.txtZoomLevel.Name = "txtZoomLevel";
            this.txtZoomLevel.Size = new System.Drawing.Size(60, 20);
            this.txtZoomLevel.TabIndex = 8;
            this.txtZoomLevel.Text = "100";
            this.txtZoomLevel.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;
            this.txtZoomLevel.TextChanged += new System.EventHandler(this.OnUserChange);
            // 
            // lblSectionBehavior
            // 
            this.lblSectionBehavior.AutoSize = true;
            this.lblSectionBehavior.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblSectionBehavior.Location = new System.Drawing.Point(16, 175);
            this.lblSectionBehavior.Name = "lblSectionBehavior";
            this.lblSectionBehavior.Size = new System.Drawing.Size(61, 13);
            this.lblSectionBehavior.TabIndex = 9;
            this.lblSectionBehavior.Text = "Behavior";
            // 
            // chkRememberLastAddress
            // 
            this.chkRememberLastAddress.AutoSize = true;
            this.chkRememberLastAddress.Location = new System.Drawing.Point(160, 175);
            this.chkRememberLastAddress.Name = "chkRememberLastAddress";
            this.chkRememberLastAddress.Size = new System.Drawing.Size(140, 17);
            this.chkRememberLastAddress.TabIndex = 10;
            this.chkRememberLastAddress.Text = "&Remember last address";
            this.chkRememberLastAddress.UseVisualStyleBackColor = true;
            this.chkRememberLastAddress.CheckedChanged += new System.EventHandler(this.OnUserChange);
            // 
            // chkAllowBackgroundBrowser
            // 
            this.chkAllowBackgroundBrowser.AutoSize = true;
            this.chkAllowBackgroundBrowser.Location = new System.Drawing.Point(160, 198);
            this.chkAllowBackgroundBrowser.Name = "chkAllowBackgroundBrowser";
            this.chkAllowBackgroundBrowser.Size = new System.Drawing.Size(194, 17);
            this.chkAllowBackgroundBrowser.TabIndex = 11;
            this.chkAllowBackgroundBrowser.Text = "Allow browser to run in &background";
            this.chkAllowBackgroundBrowser.UseVisualStyleBackColor = true;
            this.chkAllowBackgroundBrowser.CheckedChanged += new System.EventHandler(this.OnUserChange);
            // 
            // chkAllowAudioNotifications
            // 
            this.chkAllowAudioNotifications.AutoSize = true;
            this.chkAllowAudioNotifications.Location = new System.Drawing.Point(160, 221);
            this.chkAllowAudioNotifications.Name = "chkAllowAudioNotifications";
            this.chkAllowAudioNotifications.Size = new System.Drawing.Size(132, 17);
            this.chkAllowAudioNotifications.TabIndex = 12;
            this.chkAllowAudioNotifications.Text = "Allow audio &notifications";
            this.chkAllowAudioNotifications.UseVisualStyleBackColor = true;
            this.chkAllowAudioNotifications.CheckedChanged += new System.EventHandler(this.OnUserChange);
            // 
            // chkMuteAudio
            // 
            this.chkMuteAudio.AutoSize = true;
            this.chkMuteAudio.Location = new System.Drawing.Point(160, 244);
            this.chkMuteAudio.Name = "chkMuteAudio";
            this.chkMuteAudio.Size = new System.Drawing.Size(80, 17);
            this.chkMuteAudio.TabIndex = 13;
            this.chkMuteAudio.Text = "&Mute audio";
            this.chkMuteAudio.UseVisualStyleBackColor = true;
            this.chkMuteAudio.CheckedChanged += new System.EventHandler(this.OnUserChange);
            // 
            // lblSectionPrivacy
            // 
            this.lblSectionPrivacy.AutoSize = true;
            this.lblSectionPrivacy.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblSectionPrivacy.Location = new System.Drawing.Point(16, 280);
            this.lblSectionPrivacy.Name = "lblSectionPrivacy";
            this.lblSectionPrivacy.Size = new System.Drawing.Size(49, 13);
            this.lblSectionPrivacy.TabIndex = 14;
            this.lblSectionPrivacy.Text = "Privacy";
            // 
            // chkClearBrowsingDataOnExit
            // 
            this.chkClearBrowsingDataOnExit.AutoSize = true;
            this.chkClearBrowsingDataOnExit.Location = new System.Drawing.Point(160, 280);
            this.chkClearBrowsingDataOnExit.Name = "chkClearBrowsingDataOnExit";
            this.chkClearBrowsingDataOnExit.Size = new System.Drawing.Size(148, 17);
            this.chkClearBrowsingDataOnExit.TabIndex = 15;
            this.chkClearBrowsingDataOnExit.Text = "Clear history on e&xit";
            this.chkClearBrowsingDataOnExit.UseVisualStyleBackColor = true;
            this.chkClearBrowsingDataOnExit.CheckedChanged += new System.EventHandler(this.OnUserChange);
            // 
            // lblSectionAdvanced
            // 
            this.lblSectionAdvanced.AutoSize = true;
            this.lblSectionAdvanced.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblSectionAdvanced.Location = new System.Drawing.Point(16, 315);
            this.lblSectionAdvanced.Name = "lblSectionAdvanced";
            this.lblSectionAdvanced.Size = new System.Drawing.Size(65, 13);
            this.lblSectionAdvanced.TabIndex = 16;
            this.lblSectionAdvanced.Text = "Advanced";
            // 
            // chkIsolateBrowserInstances
            // 
            this.chkIsolateBrowserInstances.AutoSize = true;
            this.chkIsolateBrowserInstances.Location = new System.Drawing.Point(160, 315);
            this.chkIsolateBrowserInstances.Name = "chkIsolateBrowserInstances";
            this.chkIsolateBrowserInstances.Size = new System.Drawing.Size(171, 17);
            this.chkIsolateBrowserInstances.TabIndex = 17;
            this.chkIsolateBrowserInstances.Text = "&Isolate this browser instance";
            this.chkIsolateBrowserInstances.UseVisualStyleBackColor = true;
            this.chkIsolateBrowserInstances.CheckedChanged += new System.EventHandler(this.OnUserChange);
            // 
            // chkEnableDevTools
            // 
            this.chkEnableDevTools.AutoSize = true;
            this.chkEnableDevTools.Location = new System.Drawing.Point(160, 338);
            this.chkEnableDevTools.Name = "chkEnableDevTools";
            this.chkEnableDevTools.Size = new System.Drawing.Size(174, 17);
            this.chkEnableDevTools.TabIndex = 18;
            this.chkEnableDevTools.Text = "Enable developer tools (&F12)";
            this.chkEnableDevTools.UseVisualStyleBackColor = true;
            this.chkEnableDevTools.CheckedChanged += new System.EventHandler(this.OnUserChange);
            // 
            // WorkspaceUserControl
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.AutoScroll = true;
            this.Controls.Add(this.chkEnableDevTools);
            this.Controls.Add(this.chkIsolateBrowserInstances);
            this.Controls.Add(this.lblSectionAdvanced);
            this.Controls.Add(this.chkClearBrowsingDataOnExit);
            this.Controls.Add(this.lblSectionPrivacy);
            this.Controls.Add(this.chkMuteAudio);
            this.Controls.Add(this.chkAllowAudioNotifications);
            this.Controls.Add(this.chkAllowBackgroundBrowser);
            this.Controls.Add(this.chkRememberLastAddress);
            this.Controls.Add(this.lblSectionBehavior);
            this.Controls.Add(this.txtZoomLevel);
            this.Controls.Add(this.lblZoomLevel);
            this.Controls.Add(this.chkHideAddress);
            this.Controls.Add(this.chkHideToolbar);
            this.Controls.Add(this.lblSectionDisplay);
            this.Controls.Add(this.txtHomeAddress);
            this.Controls.Add(this.lblHomeAddress);
            this.Controls.Add(this.txtName);
            this.Controls.Add(this.lblName);
            this.Name = "WorkspaceUserControl";
            this.Size = new System.Drawing.Size(560, 380);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label lblName;
        private System.Windows.Forms.TextBox txtName;
        private System.Windows.Forms.Label lblHomeAddress;
        private System.Windows.Forms.TextBox txtHomeAddress;
        private System.Windows.Forms.CheckBox chkRememberLastAddress;
        private System.Windows.Forms.CheckBox chkHideToolbar;
        private System.Windows.Forms.CheckBox chkHideAddress;
        private System.Windows.Forms.Label lblZoomLevel;
        private System.Windows.Forms.TextBox txtZoomLevel;
        private System.Windows.Forms.CheckBox chkClearBrowsingDataOnExit;
        private System.Windows.Forms.CheckBox chkAllowBackgroundBrowser;
        private System.Windows.Forms.CheckBox chkAllowAudioNotifications;
        private System.Windows.Forms.CheckBox chkMuteAudio;
        private System.Windows.Forms.CheckBox chkIsolateBrowserInstances;
        private System.Windows.Forms.CheckBox chkEnableDevTools;
        private System.Windows.Forms.Label lblSectionDisplay;
        private System.Windows.Forms.Label lblSectionBehavior;
        private System.Windows.Forms.Label lblSectionPrivacy;
        private System.Windows.Forms.Label lblSectionAdvanced;
    }
}
