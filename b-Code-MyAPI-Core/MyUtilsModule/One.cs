//MyUtilsModule/One.cs
using BaseVariable;

namespace MyUtilsModule; 

public class One
{
    public static string One1()
    {
        return "1";
    }   
    public static string One2() 
    {
        return "123";
    }  
    public static async Task<string> One3() 
    {
        string one1Url = "http://localhost:5100/api/MyUtilsModule/One/one1";
        string one1ResponseStr = await BSV.httpClient.GetStringAsync(one1Url);
        string one1Url2 = "http://localhost:5100/api/MyUtilsModule/One/one2";
        string one1ResponseStr2 = await BSV.httpClient.GetStringAsync(one1Url2);
        return one1ResponseStr + one1ResponseStr2;
    }
}


