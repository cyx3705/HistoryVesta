' ================================================
' VBDemoApi.vb   （示例业务类）
' ================================================
''' <summary>
''' VB 测试业务类 - 全暴露模式下会被自动扫描
''' </summary>
Public Class VBDemoApi

    ''' <summary>
    ''' 简单加法测试
    ''' </summary>
    Public Function Add(a As Integer, b As Integer) As Integer
        Return a + b
    End Function

    ''' <summary>
    ''' 问候语测试
    ''' </summary>
    Public Function Hello(name As String) As String
        Return $"你好，{name}！这是来自 VB.NET 的问候。"
    End Function

    ''' <summary>
    ''' 返回当前时间
    ''' </summary>
    Public Function GetCurrentTime() As String
        Return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
    End Function

    ''' <summary>
    ''' 测试输出
    ''' </summary>
    Public Sub TestOutput(message As String)
        Console.WriteLine($"[VB测试输出] {message}")
    End Sub

End Class
