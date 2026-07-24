' ================================================
' CmdModule - VB.NET 模块定义
' 文件名建议：CmdModule.vb
' ================================================

Imports BaseVariable
Imports System.Threading.Tasks
''' <summary>
''' CmdModule 模块信息类（VB.NET 版）
''' 提供命令行执行和控制台输出重定向到网页终端的功能
''' </summary>
Public Class ModuleInfo
    Inherits ModuleInfoBase

    ''' <summary>
    ''' 模块名称
    ''' </summary>
    Public Overrides ReadOnly Property ModuleName As String
        Get
            Return "MyVBModule"
        End Get
    End Property

    ''' <summary>
    ''' 模块描述
    ''' </summary>
    Public Overrides ReadOnly Property Description As String
        Get
            Return "提供VB语言的测试用于测试框架的VB支持效果。"
        End Get
    End Property

    ''' <summary>
    ''' 作者
    ''' </summary>
    Public Overrides ReadOnly Property Author As String
        Get
            Return "Pinavia"
        End Get
    End Property

    ''' <summary>
    ''' 版本号
    ''' </summary>
    Public Overrides ReadOnly Property Version As String
        Get
            Return "v1.0.0"
        End Get
    End Property

    ''' <summary>
    ''' 是否全暴露模式（True = 模块内所有 public 类和方法都自动注册）
    ''' </summary>
    Public Overrides ReadOnly Property Open As Boolean
        Get
            Return True
        End Get
    End Property

    ''' <summary>
    ''' 因为使用全暴露模式（Open=True），MainClassType 可不设置或保持为 Nothing
    ''' </summary>
    Public Overrides ReadOnly Property MainClassType As Type
        Get
            Return Nothing
        End Get
    End Property

    ''' <summary>
    ''' 模块初始化顺序（越小越早执行）
    ''' </summary>
    Public Overrides ReadOnly Property InitializeOrder As Integer
        Get
            Return 15
        End Get
    End Property

    ''' <summary>
    ''' 初始化地址列表（关键：把需要执行的初始化操作写在这里）
    ''' </summary>
    Public Overrides ReadOnly Property InitAddresses As List(Of String)
        Get
            Return New List(Of String) From {
            "/api/MyVBModule/VBDemoApi/Hello"   ' ← 把原来的 demoApi.Hello("初始化") 改成接口地址
        }
        End Get
    End Property

End Class
