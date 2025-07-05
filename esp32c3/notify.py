import os
import subprocess
Import("env")

def notify_success(source, target, env):
    subprocess.run([
        "powershell",
        "-Command",
        "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime];" +
        "$template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText01);" +
        "$textNodes = $template.GetElementsByTagName('text');" +
        "$textNodes.Item(0).AppendChild($template.CreateTextNode('PlatformIO Build Succeeded'));" +
        "$toast = [Windows.UI.Notifications.ToastNotification]::new($template);" +
        "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('PlatformIO').Show($toast);"
    ])

env.AddPostAction("buildprog", notify_success)
