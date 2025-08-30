Add-Type -TypeDefinition @"
using System;
using System.Reflection;
using System.Reflection.Emit;

public class MockDllCreator {
    public static void CreateMockDll(string outputPath) {
        AssemblyName assemblyName = new AssemblyName("FyteClub");
        assemblyName.Version = new Version(1, 0, 0, 0);
        
        AssemblyBuilder assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(
            assemblyName, AssemblyBuilderAccess.Save);
        
        ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule(
            "FyteClub", "FyteClub.dll");
        
        TypeBuilder typeBuilder = moduleBuilder.DefineType(
            "FyteClub.FyteClubPlugin", 
            TypeAttributes.Public | TypeAttributes.Class);
        
        typeBuilder.CreateType();
        assemblyBuilder.Save("FyteClub.dll");
    }
}
"@

[MockDllCreator]::CreateMockDll("FyteClub.dll")
Write-Host "Created mock FyteClub.dll"