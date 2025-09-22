using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System.Runtime.CompilerServices;

namespace FyteClub.ModSystem.Advanced;

public unsafe class CharacterChangeDetector
{
    private byte[] _customizeData = new byte[26];
    private byte[] _equipSlotData = new byte[40];
    private ushort[] _mainHandData = new ushort[3];
    private ushort[] _offHandData = new ushort[3];
    private byte _classJob = 0;

    public bool DetectChanges(ICharacter character)
    {
        if (character.Address == IntPtr.Zero) return false;

        var chara = (Character*)character.Address;
        var drawObj = chara->GameObject.DrawObject;
        if (drawObj == null) return false;

        bool hasChanges = false;

        // Check equipment changes
        if (((DrawObject*)drawObj)->Object.GetObjectType() == ObjectType.CharacterBase
            && ((CharacterBase*)drawObj)->GetModelType() == CharacterBase.ModelType.Human)
        {
            hasChanges |= CompareAndUpdateEquipData((byte*)&((Human*)drawObj)->Head);
            hasChanges |= CompareAndUpdateWeaponData(chara);
        }
        else
        {
            hasChanges |= CompareAndUpdateEquipData((byte*)Unsafe.AsPointer(ref chara->DrawData.EquipmentModelIds[0]));
        }

        // Check customize changes
        if (((DrawObject*)drawObj)->Object.GetObjectType() == ObjectType.CharacterBase
            && ((CharacterBase*)drawObj)->GetModelType() == CharacterBase.ModelType.Human)
        {
            hasChanges |= CompareAndUpdateCustomizeData(((Human*)drawObj)->Customize.Data);
        }
        else
        {
            hasChanges |= CompareAndUpdateCustomizeData(chara->DrawData.CustomizeData.Data);
        }

        // Check class job changes
        var classJob = chara->CharacterData.ClassJob;
        if (classJob != _classJob)
        {
            _classJob = classJob;
            hasChanges = true;
        }

        return hasChanges;
    }

    public bool IsBeingDrawn(ICharacter character)
    {
        if (character.Address == IntPtr.Zero) return true;

        var chara = (Character*)character.Address;
        var drawObj = chara->GameObject.DrawObject;
        if (drawObj == null) return true;

        // Check render flags
        if (chara->GameObject.RenderFlags != 0x0) return true;

        // Check model loading state for players
        if (((DrawObject*)drawObj)->Object.GetObjectType() == ObjectType.CharacterBase)
        {
            var charBase = (CharacterBase*)drawObj;
            if (charBase->HasModelInSlotLoaded != 0) return true;
            if (charBase->HasModelFilesInSlotLoaded != 0) return true;
        }

        return false;
    }

    private bool CompareAndUpdateEquipData(byte* equipSlotData)
    {
        bool hasChanges = false;
        for (int i = 0; i < _equipSlotData.Length; i++)
        {
            var data = equipSlotData[i];
            if (_equipSlotData[i] != data)
            {
                _equipSlotData[i] = data;
                hasChanges = true;
            }
        }
        return hasChanges;
    }

    private bool CompareAndUpdateCustomizeData(Span<byte> customizeData)
    {
        bool hasChanges = false;
        for (int i = 0; i < customizeData.Length && i < _customizeData.Length; i++)
        {
            var data = customizeData[i];
            if (_customizeData[i] != data)
            {
                _customizeData[i] = data;
                hasChanges = true;
            }
        }
        return hasChanges;
    }

    private bool CompareAndUpdateWeaponData(Character* chara)
    {
        bool hasChanges = false;

        ref var mh = ref chara->DrawData.Weapon(DrawDataContainer.WeaponSlot.MainHand);
        ref var oh = ref chara->DrawData.Weapon(DrawDataContainer.WeaponSlot.OffHand);

        var mainWeapon = (Weapon*)mh.DrawObject;
        var offWeapon = (Weapon*)oh.DrawObject;

        if (mainWeapon != null)
        {
            hasChanges |= mainWeapon->ModelSetId != _mainHandData[0];
            _mainHandData[0] = mainWeapon->ModelSetId;
            hasChanges |= mainWeapon->Variant != _mainHandData[1];
            _mainHandData[1] = mainWeapon->Variant;
            hasChanges |= mainWeapon->SecondaryId != _mainHandData[2];
            _mainHandData[2] = mainWeapon->SecondaryId;
        }

        if (offWeapon != null)
        {
            hasChanges |= offWeapon->ModelSetId != _offHandData[0];
            _offHandData[0] = offWeapon->ModelSetId;
            hasChanges |= offWeapon->Variant != _offHandData[1];
            _offHandData[1] = offWeapon->Variant;
            hasChanges |= offWeapon->SecondaryId != _offHandData[2];
            _offHandData[2] = offWeapon->SecondaryId;
        }

        return hasChanges;
    }
}