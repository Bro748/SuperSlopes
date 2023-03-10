using DevInterface;
using MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

using System.Security;
using System.Security.Permissions;
using System.Reflection;

[module: UnverifiableCode]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]

namespace SuperSlopes.Utils
{
    internal static class PlacedObjectsManager
    {
        #region HOOKS
        private static bool _hooked = false;
        /// <summary>
        /// Applies the necessary hooks for the framework to do its thing.
        /// Called when any managed object is registered.
        /// </summary>
        private static void Apply()
        {
            if (_hooked) return;
            _hooked = true;

            On.PlacedObject.GenerateEmptyData += PlacedObject_GenerateEmptyData_Patch;
            On.Room.Loaded += Room_Loaded_Patch;
            On.DevInterface.ObjectsPage.CreateObjRep += ObjectsPage_CreateObjRep_Patch;

            //PlacedObjectsExample();
        }

        private static void ObjectsPage_CreateObjRep_Patch(On.DevInterface.ObjectsPage.orig_CreateObjRep orig, ObjectsPage self, PlacedObject.Type tp, PlacedObject pObj)
        {
            orig(self, tp, pObj);
            ManagedObjectType manager = GetManagerForType(tp);
            if (manager == null) return;

            if (pObj == null) pObj = self.RoomSettings.placedObjects[self.RoomSettings.placedObjects.Count - 1];
            PlacedObjectRepresentation placedObjectRepresentation = manager.MakeRepresentation(pObj, self);
            if (placedObjectRepresentation == null) return;

            PlacedObjectRepresentation old = (PlacedObjectRepresentation)self.tempNodes.Pop();
            self.subNodes.Pop();
            old.ClearSprites();
            self.tempNodes.Add(placedObjectRepresentation);
            self.subNodes.Add(placedObjectRepresentation);
        }

        public static bool CurrentRoomFirstTimeRealized(Room room)
        {
            if (!firstTimeRealizedKVP.Equals(new KeyValuePair<Room, bool>()) && firstTimeRealizedKVP.Key == room)
            {
                return firstTimeRealizedKVP.Value;
            }

            return false;
        }

        private static KeyValuePair<Room, bool> firstTimeRealizedKVP = new KeyValuePair<Room, bool>();

        private static void Room_Loaded_Patch(On.Room.orig_Loaded orig, Room self)
        {
            firstTimeRealizedKVP = new KeyValuePair<Room, bool>(self, self.abstractRoom.firstTimeRealized);
            orig(self);
            if (self.game is null) return;

            ManagedObjectType manager;
            for (int i = 0; i < self.roomSettings.placedObjects.Count; i++)
            {
                if (self.roomSettings.placedObjects[i].active)
                {
                    if ((manager = GetManagerForType(self.roomSettings.placedObjects[i].type)) != null)
                    {
                        UpdatableAndDeletable obj = manager.MakeObject(self.roomSettings.placedObjects[i], self);
                        if (obj == null) continue;
                        self.AddObject(obj);
                    }
                }
            }
        }

        private static void PlacedObject_GenerateEmptyData_Patch(On.PlacedObject.orig_GenerateEmptyData orig, PlacedObject self)
        {
            orig(self);
            ManagedObjectType manager = GetManagerForType(self.type);
            if (manager != null)
            {
                self.data = manager.MakeEmptyData(self);
            }
        }

        #endregion HOOKS

        #region NATIVEDETOURS
        private static bool _stringsdetoured = false;
        /// <summary>
        /// Applies the Input detours required for text input.
        /// Called when any string input reprs are created.
        /// </summary>
        private static void SetupInputDetours()
        {
            if (_stringsdetoured) return;
            _stringsdetoured = true;

            BindingFlags bindingFlags =
                    BindingFlags.NonPublic | BindingFlags.Static;
            MethodBase getKeyMethod;
            MethodBase captureInputMethod;

            getKeyMethod = typeof(Input).GetMethod("GetKey", new Type[] { typeof(string) });
            captureInputMethod = typeof(PlacedObjectsManager)
                    .GetMethod("CaptureInput", bindingFlags, null, new Type[] { typeof(string) }, null);
            inputDetour_string = new NativeDetour(getKeyMethod, captureInputMethod);

            getKeyMethod = typeof(Input).GetMethod("GetKey", new Type[] { typeof(KeyCode) });
            captureInputMethod = typeof(PlacedObjectsManager)
                    .GetMethod("CaptureInput", bindingFlags, null, new Type[] { typeof(KeyCode) }, null);
            inputDetour_code = new NativeDetour(getKeyMethod, captureInputMethod);

            On.RainWorldGame.RawUpdate += RainWorldGame_RawUpdate;
        }

        private static void RainWorldGame_RawUpdate(On.RainWorldGame.orig_RawUpdate orig, RainWorldGame self, float dt)
        {
            orig(self, dt);
            if (self.devUI == null)
            {
                ManagedStringControl.activeStringControl = null;     // remove string control focus when dev tools are closed
            }
        }


#pragma warning disable IDE0051 // Reflection, dearling
        private static NativeDetour inputDetour_string;
        private static NativeDetour inputDetour_code;

        private static void UndoInputDetours()
        {
            inputDetour_string.Undo();
            inputDetour_code.Undo();
        }

        private static bool CaptureInput(string key)
        {
            key = key.ToUpper();
            KeyCode code = (KeyCode)Enum.Parse(typeof(KeyCode), key);
            return CaptureInput(code);
        }
#pragma warning restore IDE0051 // Remove unused private members

        private static bool CaptureInput(KeyCode code)
        {
            bool res;

            if (ManagedStringControl.activeStringControl == null)
            {
                res = Orig_GetKey(code);
            }
            else
            {
                if (code == KeyCode.Escape)
                {
                    res = Orig_GetKey(code);
                }
                else
                {
                    res = false;
                }
            }

            return res;
        }

        private static bool Orig_GetKey(KeyCode code)
        {
            inputDetour_code.Undo();
            bool res = Input.GetKey(code);
            inputDetour_code.Apply();
            return res;
        }

        #endregion NATIVEDETOURS

        #region INTERNALS
        private static readonly List<ManagedObjectType> managedObjectTypes = new List<ManagedObjectType>();
        /// <summary>
        /// Called from the hooks, finds the manager for the type, if any.
        /// </summary>
        private static ManagedObjectType GetManagerForType(PlacedObject.Type tp)
        {
            foreach (var manager in managedObjectTypes)
            {
                if (tp == manager.GetObjectType() && tp != PlacedObject.Type.None) return manager;
            }
            return null;
        }

        private static PlacedObject.Type DeclareOrGetEnum(string name)
        {
            // enum handling is delayed because of how enumextend works
            // bee needs to let mods add enums during onload or this will be always super annoying to use
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException("name is empty");
            // nope, this crashes because enumextender hasnt readied its pants yet
            //if (PastebinMachine.EnumExtender.EnumExtender.declarations.Count > 0) PastebinMachine.EnumExtender.EnumExtender.ExtendEnumsAgain();
            PlacedObject.Type tp;
            try
            {
                tp = (PlacedObject.Type)Enum.Parse(typeof(PlacedObject.Type), name);
            }
            catch
            {
                PastebinMachine.EnumExtender.EnumExtender.AddDeclaration(typeof(PlacedObject.Type), name);
                PastebinMachine.EnumExtender.EnumExtender.ExtendEnumsAgain();
                tp = (PlacedObject.Type)Enum.Parse(typeof(PlacedObject.Type), name);
            }

            return tp;
        }
        #endregion INTERNALS

        /// <summary>
        /// Register a <see cref="ManagedObjectType"/> or <see cref="FullyManagedObjectType"/> to handle object, data and repr initialization during room load and devtools hooks
        /// </summary>
        /// <param name="obj"></param>
        public static void RegisterManagedObject(ManagedObjectType obj)
        {
            Apply();
            managedObjectTypes.Add(obj);
        }
        /// <summary>
        /// Same as <see cref="RegisterManagedObject(ManagedObjectType)"/>, but with generics.
        /// </summary>
        /// <typeparam name="UAD"></typeparam>
        /// <typeparam name="DATA"></typeparam>
        /// <typeparam name="REPR"></typeparam>
        /// <param name="key"></param>
        /// <param name="singleInstance"></param>
        public static void RegisterManagedObject<UAD, DATA, REPR>(string key, bool singleInstance = false)
            where UAD : UpdatableAndDeletable
            where DATA : ManagedData
            where REPR : ManagedRepresentation
        {
            RegisterManagedObject(new ManagedObjectType(key, typeof(UAD), typeof(DATA), typeof(REPR), singleInstance));
        }

        /// <summary>
        /// Shorthand for registering a <see cref="FullyManagedObjectType"/>.
        /// Wraps an UpdateableAndDeletable into a managed type with managed data and UI.
        /// Can also be used with a null type to spawn a Managed data+representation with no object on room.load
        /// If the object isn't null its Constructor should take (Room, PlacedObject) or (PlacedObject, Room).
        /// </summary>
        /// <param name="managedFields"></param>
        /// <param name="type">An UpdateableAndDeletable</param>
        /// <param name="name">Optional enum-name for your object, otherwise infered from type. Can be an enum already created with Enumextend. Do NOT use enum.ToString() on an enumextend'd enum, it wont work during Init() or Load()</param>
        public static void RegisterFullyManagedObjectType(ManagedField[] managedFields, Type type, string name = null)
        {
            if (string.IsNullOrEmpty(name)) name = type.Name;

            ManagedObjectType fullyManaged = new FullyManagedObjectType(name, type, managedFields);
            RegisterManagedObject(fullyManaged);
        }

        /// <summary>
        /// Shorthand for registering a ManagedObjectType with no actual object.
        /// Creates an empty data-holding placed object.
        /// Data and Repr must work well together (typically rep tries to cast data to a specific type to use it).
        /// Either can be left null, so no data or no specific representation will be created for the placedobject.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="dataType"></param>
        /// <param name="reprType"></param>
        public static void RegisterEmptyObjectType(string name, Type dataType, Type reprType)
        {
            ManagedObjectType emptyObjectType = new ManagedObjectType(name, null, dataType, reprType);
            RegisterManagedObject(emptyObjectType);
        }
        /// <summary>
        /// Same as <see cref="RegisterEmptyObjectType(string, Type, Type)"/>, but with generics.
        /// </summary>
        /// <typeparam name="DATA"></typeparam>
        /// <typeparam name="REPR"></typeparam>
        /// <param name="key"></param>
        public static void RegisterEmptyObjectType<DATA, REPR>(string key)
            where DATA : ManagedData
            where REPR : ManagedRepresentation
        {
            RegisterEmptyObjectType(key, typeof(DATA), typeof(REPR));
        }

        #region MANAGED
        /// <summary>
        /// Main class for managed object types.
        /// Make-calls CAN return null, causing the object to not be created, or have no data, or use the default handle representation.
        /// This class can be used to simply handle the room/devtools hooks.
        /// Call <see cref="RegisterManagedObject(ManagedObjectType)"/> to register your manager
        /// </summary>
        public class ManagedObjectType
        {
            protected PlacedObject.Type placedType;
            protected readonly string name;
            protected readonly Type objectType;
            protected readonly Type dataType;
            protected readonly Type reprType;
            protected readonly bool singleInstance;
            /// <summary>
            /// Creates a ManagedObjectType responsible for creating your placedobject instance, data and repr
            /// </summary>
            /// <param name="name">The enum-name this manager responds for. Do NOT use EnumExt_MyEnum.MyObject.ToString() because on mod-loading enumextender might not have run yet and your enums aren't extended.</param>
            /// <param name="objectType">The Type of your UpdateableAndDeletable object. Must have a constructor like (Room room, PlacedObject pObj) or (PlacedObject pObj, Room room), (PlacedObject pObj) or (Room room).</param>
            /// <param name="dataType">The Type of your PlacedObject.Data. Must have a constructor like (PlacedObject pObj).</param>
            /// <param name="reprType">The Type of your PlacedObjectRepresentation. Must have a constructor like (DevUI owner, string IDstring, DevUINode parentNode, PlacedObject pObj, string name) or (PlacedObject.Type placedType, ObjectsPage objPage, PlacedObject pObj).</param>
            /// <param name="singleInstance">Wether only one of this object should be created per room. Corruption-object that scans for other placedobjects style.</param>
            public ManagedObjectType(string name, Type objectType, Type dataType, Type reprType, bool singleInstance = false)
            {
                placedType = default; // type parsing deferred until actualy used, due to enumextend deferred initialization
                this.name = name;
                this.objectType = objectType;
                this.dataType = dataType;
                this.reprType = reprType;
                this.singleInstance = singleInstance;
            }

            /// <summary>
            /// The <see cref="PlacedObject.Type"/> this is the manager for.
            /// Only call this after rainworld.start call otherwise EnumExtender might not be available.
            /// Store a reference to your <see cref="ManagedObjectType"/> instead of the enum type.
            /// </summary>
            public virtual PlacedObject.Type GetObjectType()
            {
                return placedType == default ? placedType = DeclareOrGetEnum(name) : placedType;
            }

            /// <summary>
            /// Called from Room.Loaded hook
            /// </summary>
            public virtual UpdatableAndDeletable MakeObject(PlacedObject placedObject, Room room)
            {
                if (objectType == null) return null;

                if (singleInstance) // Only one per room
                {
                    UpdatableAndDeletable instance = null;
                    foreach (var item in room.updateList)
                    {
                        if (item.GetType().IsAssignableFrom(objectType))
                        {
                            instance = item;
                            break;
                        }
                    }
                    if (instance != null) return null;
                }

                try { return (UpdatableAndDeletable)Activator.CreateInstance(objectType, new object[] { room, placedObject }); }
                catch (MissingMethodException)
                {
                    try { return (UpdatableAndDeletable)Activator.CreateInstance(objectType, new object[] { placedObject, room }); }
                    catch (MissingMethodException)
                    {
                        try { return (UpdatableAndDeletable)Activator.CreateInstance(objectType, new object[] { placedObject }); }
                        catch (MissingMethodException)
                        {
                            try { return (UpdatableAndDeletable)Activator.CreateInstance(objectType, new object[] { room }); } // Objects that scan room for data or no data;
                            catch (MissingMethodException) { throw new ArgumentException("ManagedObjectType.MakeObject : objectType " + objectType.Name + " must have a constructor like (Room room, PlacedObject pObj) or (PlacedObject pObj, Room room) or (Room room)"); }
                        }
                    }
                }
            }

            /// <summary>
            /// Called from PlacedObject.GenerateEmptyData hook
            /// </summary>
            public virtual PlacedObject.Data MakeEmptyData(PlacedObject pObj)
            {
                if (dataType == null) return null;

                try { return (PlacedObject.Data)Activator.CreateInstance(dataType, new object[] { pObj }); }
                catch (MissingMethodException)
                {
                    try { return (PlacedObject.Data)Activator.CreateInstance(dataType, new object[] { pObj, PlacedObject.LightFixtureData.Type.RedLight }); } // Redlights man
                    catch (MissingMethodException) { throw new ArgumentException("ManagedObjectType.MakeEmptyData : dataType " + dataType.Name + " must have a constructor like (PlacedObject pObj)"); }
                }
            }

            /// <summary>
            /// Called from ObjectsPage.CreateObjRep hook
            /// </summary>
            public virtual PlacedObjectRepresentation MakeRepresentation(PlacedObject pObj, ObjectsPage objPage)
            {
                if (reprType == null) return null;

                try { return (PlacedObjectRepresentation)Activator.CreateInstance(reprType, new object[] { objPage.owner, placedType.ToString() + "_Rep", objPage, pObj, placedType.ToString() }); }
                catch (MissingMethodException)
                {
                    try { return (PlacedObjectRepresentation)Activator.CreateInstance(reprType, new object[] { objPage.owner, placedType.ToString() + "_Rep", objPage, pObj, placedType.ToString(), false }); } // Resizeables man
                    catch (MissingMethodException)
                    {
                        try { return (PlacedObjectRepresentation)Activator.CreateInstance(reprType, new object[] { pObj.type, objPage, pObj }); } // Our own silly types
                        catch (MissingMethodException) { throw new ArgumentException("ManagedObjectType.MakeRepresentation : reprType " + reprType.Name + " must have a constructor like (DevUI owner, string IDstring, DevUINode parentNode, PlacedObject pObj, string name) or (PlacedObject.Type placedType, ObjectsPage objPage, PlacedObject pObj)"); }
                    }
                }
            }
        }

        /// <summary>
        /// Class for managing a wraped <see cref="UpdatableAndDeletable"/> object
        /// Uses the fully managed data and representation types <see cref="ManagedData"/> and <see cref="ManagedRepresentation"/>
        /// </summary>
        public class FullyManagedObjectType : ManagedObjectType
        {
            protected readonly ManagedField[] managedFields;

            public FullyManagedObjectType(string name, Type objectType, ManagedField[] managedFields, bool singleInstance = false) : base(name, objectType, null, null, singleInstance)
            {
                this.managedFields = managedFields;
            }

            public override PlacedObject.Data MakeEmptyData(PlacedObject pObj)
            {
                return new ManagedData(pObj, managedFields);
            }

            public override PlacedObjectRepresentation MakeRepresentation(PlacedObject pObj, ObjectsPage objPage)
            {
                return new ManagedRepresentation(GetObjectType(), objPage, pObj);
            }
        }

        /// <summary>
        /// A field to handle serialization and generate UI for your data, for use with <see cref="ManagedData"/>.
        /// A field is merely a recipe/interface, the actual data is stored in the <see cref="ManagedData"/> data object for each pObj.
        /// You can use a field as an <see cref="Attribute"/> anotating data fields in your class that inherits <see cref="ManagedData"/> so they stay in sync.
        /// </summary>
        [AttributeUsage(AttributeTargets.Field)]
        public abstract class ManagedField : Attribute
        {
            public readonly string key;
            protected object defaultValue; // Removed reaonly, now one can be clever with defaults that apply to the next object being placed and such.
            public virtual object DefaultValue => defaultValue; // I SUSPECT one day someone will run into a situation with enums where the default value doesn't exist at initialization. Enumextend moment. Lets be nice to that poor soul.

            protected ManagedField(string key, object defaultValue)
            {
                this.key = key;
                this.defaultValue = defaultValue;
            }

            /// <summary>
            /// Serialization method called from <see cref="ManagedData.ToString"/>
            /// </summary>
            public virtual string ToString(object value) => value.ToString();

            /// <summary>
            /// Deserialization method called from <see cref="ManagedData.FromString"/>. Don't forget to sanitize your data.
            /// </summary>
            public abstract object FromString(string str);

            /// <summary>
            /// Wether this field spawns a control panel node or not. Inherit <see cref="ManagedFieldWithPanel"/> for actually creating them.
            /// </summary>
            public virtual bool NeedsControlPanel { get => this is ManagedFieldWithPanel; }

            /// <summary>
            /// Create an aditional DevUINode for manipulating this field. Inherit a PositionedDevUINode if you need to create several sub-nodes.
            /// </summary>
            public virtual DevUINode MakeAditionalNodes(ManagedData managedData, ManagedRepresentation managedRepresentation)
            {
                return null;
            }

            // Stop inheriting crap :/
            public sealed override bool IsDefaultAttribute() { return base.IsDefaultAttribute(); }
            public sealed override bool Match(object obj) { return base.Match(obj); }
            public sealed override object TypeId => base.TypeId;
        }

        /// <summary>
        /// A <see cref="ManagedField"/> that can generate a control-panel control, for use with <see cref="ManagedRepresentation"/>
        /// </summary>
        public abstract class ManagedFieldWithPanel : ManagedField
        {
            protected readonly ControlType control;
            public readonly string displayName;

            protected ManagedFieldWithPanel(string key, object defaultValue, ControlType control = ControlType.none, string displayName = null) : base(key, defaultValue)
            {
                this.control = control;
                this.displayName = displayName ?? key;
            }

            public enum ControlType
            {
                none,
                slider,
                arrows,
                button,
                text,
                EnumButton
            }

            /// <summary>
            /// Used internally for control panel display. Consumed by <see cref="ManagedRepresentation.MakeControls"/> to expand the panel and space controls.
            /// </summary>
            public virtual Vector2 SizeOfPanelUiMinusName()
            {
                return SizeOfPanelNode() + new Vector2(SizeOfLargestDisplayValue(), 0f);
            }

            /// <summary>
            /// Used internally for control panel display. Consumed by <see cref="SizeOfPanelUiMinusName"/> and final UI nodes.
            /// </summary>
            public abstract float SizeOfLargestDisplayValue();

            /// <summary>
            /// Approx size of the UI minus displayname and valuedisplay width. Consumed by <see cref="SizeOfPanelUiMinusName"/>.
            /// </summary>
            protected virtual Vector2 SizeOfPanelNode()
            {
                switch (control)
                {
                    case ControlType.slider:
                        return new Vector2(116f, 20f);

                    case ControlType.arrows:
                        return new Vector2(52f, 20f);

                    case ControlType.text:
                    case ControlType.button:
                        return new Vector2(12f, 20f);

                    default:
                        break;
                }
                return new Vector2(0f, 20f);
            }

            /// <summary>
            /// Used internally for control panel display.
            /// Called from <see cref="ManagedRepresentation.MakeControls"/>
            /// </summary>
            public virtual float SizeOfDisplayname()
            {
                return HUD.DialogBox.meanCharWidth * (displayName.Length + 2);
            }

            /// <summary>
            /// Used internally for building the control panel display.
            /// Called from <see cref="ManagedRepresentation.MakeControls"/>
            /// </summary>
            public virtual PositionedDevUINode MakeControlPanelNode(ManagedData managedData, ManagedControlPanel panel, float sizeOfDisplayname)
            {
                switch (control)
                {
                    case ControlType.slider:
                        return new ManagedSlider(this, managedData, panel, sizeOfDisplayname);
                    case ControlType.arrows:
                        return new ManagedArrowSelector(this, managedData, panel, sizeOfDisplayname);
                    case ControlType.button:
                        return new ManagedButton(this, managedData, panel, sizeOfDisplayname);
                    case ControlType.text:
                        return new ManagedStringControl(this, managedData, panel, sizeOfDisplayname);
                    case ControlType.EnumButton:
                        return new ManagedEnumButton(this, managedData, panel, sizeOfDisplayname);
                }
                return null;
            }

            /// <summary>
            /// Used internally for controls in the panel to display the value of this field as text.
            /// </summary>
            public virtual string DisplayValueForNode(PositionedDevUINode node, ManagedData data)
            {
                // field tostring, but fields can format on their own
                return ToString(data.GetValue<object>(key));
            }

            /// <summary>
            /// Used internally for text input parsing by <see cref="ManagedStringControl"/>.
            /// Should raise an <see cref="ArgumentException"/> if the value is invalid or can't be parsed (used for visual feedback on text input)
            /// </summary>
            public virtual void ParseFromText(PositionedDevUINode node, ManagedData data, string newValue)
            {
                data.SetValue(key, FromString(newValue));
            }
        }


        /// <summary>
        /// Managed data type, handles managed fields passed through the constuctor and through Attributes.
        /// </summary>
        public class ManagedData : PlacedObject.Data
        {
            public readonly ManagedField[] fields;
            protected readonly Dictionary<string, FieldInfo> fieldInfosByKey;
            protected readonly Dictionary<string, ManagedField> fieldsByKey;
            protected readonly Dictionary<string, object> valuesByKey;

            /// <summary>
            /// Attribute for tying a field to a <see cref="ManagedField"/> that cannot be properly initialized as Attribute such as <see cref="Vector2Field"/> and <see cref="EnumField"/>.
            /// </summary>
            [AttributeUsage(AttributeTargets.Field)]
            protected class BackedByField : Attribute
            {
                public string key;

                public BackedByField(string key)
                {
                    this.key = key;
                }
            }

            /// <summary>
            /// Instantiates the managed data object for use with a placed object in the roomSettings.
            /// You shouldn't instantiate this on your own, it'll be called by the framework.
            /// </summary>
            /// <param name="owner">the <see cref="PlacedObject"/> this data belongs to</param>
            /// <param name="paramFields">the <see cref="ManagedField"/>s for this data. Upon initialization it'll also scan for any annotated fields.</param>
            public ManagedData(PlacedObject owner, ManagedField[] paramFields) : base(owner)
            {
                paramFields = paramFields ?? new ManagedField[0];
                fields = paramFields;
                fieldsByKey = new Dictionary<string, ManagedField>();
                valuesByKey = new Dictionary<string, object>();
                fieldInfosByKey = new Dictionary<string, FieldInfo>();

                panelPos = new Vector2(100, 50);

                // Scan for annotated fields
                List<ManagedField> attrFields = new List<ManagedField>();
                foreach (FieldInfo fieldInfo in GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    object[] customAttributes = fieldInfo.GetCustomAttributes(typeof(ManagedField), true);

                    foreach (var attr in customAttributes) // There should be only one or zero anyways
                    {
                        ManagedField fieldAttr = attr as ManagedField;
                        attrFields.Add(fieldAttr);
                        fieldInfosByKey[fieldAttr.key] = fieldInfo;
                        fieldInfo.SetValue(this, fieldAttr.DefaultValue);
                    }
                }
                if (attrFields.Count > 0) // any annotated fields
                {
                    attrFields.Sort((f1, f2) => string.Compare(f1.key, f2.key)); // type.GetFields() does NOT guarantee order
                    fields = paramFields.Concat(attrFields).ToArray();
                }

                // go through all fields, passed as parameter or annotated
                NeedsControlPanel = false;
                foreach (var field in fields)
                {
                    if (fieldsByKey.ContainsKey(field.key)) throw new ArgumentException("Fields with duplicated names : " + field.key);
                    fieldsByKey[field.key] = field;
                    valuesByKey[field.key] = field.DefaultValue;
                    if (field.NeedsControlPanel) NeedsControlPanel = true;
                }

                // link backed fields
                foreach (FieldInfo fieldInfo in GetType().GetFields())
                {
                    object[] customAttributes = fieldInfo.GetCustomAttributes(typeof(BackedByField), true);

                    foreach (var attr in customAttributes) // There should be only one anyways ??? As long as they have different keys everything will be fiiiiine
                    {
                        BackedByField fieldAttr = attr as BackedByField;
                        if (!fieldsByKey.ContainsKey(fieldAttr.key)) throw new ArgumentException("No such field for BackedByField : " + fieldAttr.key + ". Are you sure you created this field ?");
                        if (fieldInfosByKey.ContainsKey(fieldAttr.key)) throw new ArgumentException("BackedByField for field already backing another field : " + fieldAttr.key);
                        fieldInfosByKey[fieldAttr.key] = fieldInfo;
                        fieldInfo.SetValue(this, valuesByKey[fieldAttr.key]);
                    }
                }
            }

            public Vector2 panelPos;
            public virtual bool NeedsControlPanel { get; protected set; }
            /// <summary>
            /// For classes that inherit this to know where their data begins. Create something similar for your class if you intend it to be inherited further ;)
            /// </summary>
            protected int FieldsWhenSerialized => fields.Length + (NeedsControlPanel ? 2 : 0);

            /// <summary>
            /// Retrieves the value stored for the field represented by this key.
            /// </summary>
            public virtual T GetValue<T>(string fieldName)
            {
                if (fieldInfosByKey.TryGetValue(fieldName, out FieldInfo field))
                    return (T)field.GetValue(this);
                else
                    return (T)valuesByKey[fieldName];
            }

            /// <summary>
            /// Stores a new value for the field represented by this key. Used mostly by the managed UI. Changes are only saved when the Save button is clicked on the devtools ui
            /// </summary>
            public virtual void SetValue<T>(string fieldName, T value)
            {
                if (fieldInfosByKey.TryGetValue(fieldName, out FieldInfo field))
                    field.SetValue(this, value);
                else
                    valuesByKey[fieldName] = value;
            }

            /// <summary>
            /// Deserialization function called when the placedobject for this data is loaded
            /// </summary>
            public override void FromString(string s)
            {
                string[] array = Regex.Split(s, "~");
                int datastart = 0;
                if (NeedsControlPanel)
                {
                    panelPos = new Vector2(float.Parse(array[0]), float.Parse(array[1]));
                    datastart = 2;
                }

                for (int i = 0; i < fields.Length; i++)
                {
                    if (array.Length == datastart + i)
                    {
                        Debug.LogError("Error: Not enough fields for managed data type for " + owner.type.ToString() + "\nMaybe there's a version missmatch between the settings and the running version of the mod.");
                        break;
                    }
                    try
                    {
                        object val = fields[i].FromString(array[datastart + i]);
                        SetValue(fields[i].key, val);
                    }
                    catch (Exception)
                    {
                        Debug.LogError("Error parsing field " + fields[i].key + " from managed data type for " + owner.type.ToString() + "\nMaybe there's a version missmatch between the settings and the running version of the mod.");
                    }
                }
            }

            /// <summary>
            /// Serialization function called when the placedobject for this data is saved with devtools.
            /// </summary>
            public override string ToString()
            {
                return (NeedsControlPanel ? panelPos.x.ToString() + "~" + panelPos.y.ToString() + "~" : "") + string.Join("~", Array.ConvertAll(fields, f => f.ToString(GetValue<object>(f.key))));
            }
        }

        /// <summary>
        /// Class that manages the PlacedObjectRepresentation for a <see cref="ManagedData"/>, 
        /// creating controls for any <see cref="ManagedField"/> that needs them,
        /// or panel UI for <see cref="ManagedFieldWithPanel"/>.
        /// </summary>
        public class ManagedRepresentation : PlacedObjectRepresentation
        {
            protected readonly PlacedObject.Type placedType;
            public readonly Dictionary<string, DevUINode> managedNodes; // Unused for now, but seems convenient for specialization
            protected ManagedControlPanel panel; // Unused for now, but seems convenient for specialization

            public ManagedRepresentation(PlacedObject.Type placedType, ObjectsPage objPage, PlacedObject pObj) : base(objPage.owner, placedType.ToString() + "_Rep", objPage, pObj, placedType.ToString())
            {
                this.placedType = placedType;
                this.pObj = pObj;

                managedNodes = new Dictionary<string, DevUINode>();
                MakeControls();
            }

            protected virtual void MakeControls()
            {
                ManagedData data = pObj.data as ManagedData;
                if (data.NeedsControlPanel)
                {
                    ManagedControlPanel panel = new ManagedControlPanel(owner, "ManagedControlPanel", this, data.panelPos, Vector2.zero, pObj.type.ToString());
                    this.panel = panel;
                    subNodes.Add(panel);
                    Vector2 uiSize = new Vector2(0f, 0f);
                    Vector2 uiPos = new Vector2(3f, 3f);
                    float largestDisplayname = 0f;
                    for (int i = 0; i < data.fields.Length; i++) // up down
                    {
                        if (data.fields[i] is ManagedFieldWithPanel field && field.NeedsControlPanel)
                        {
                            largestDisplayname = Mathf.Max(largestDisplayname, field.SizeOfDisplayname());
                        }
                    }

                    for (int i = data.fields.Length - 1; i >= 0; i--) // down up
                    {
                        if (data.fields[i] is ManagedFieldWithPanel field && field.NeedsControlPanel)
                        {
                            PositionedDevUINode node = field.MakeControlPanelNode(data, panel, largestDisplayname);
                            panel.managedNodes[field.key] = node;
                            panel.managedFields[field.key] = field;
                            panel.subNodes.Add(node);
                            node.pos = uiPos;
                            uiSize.x = Mathf.Max(uiSize.x, field.SizeOfPanelUiMinusName().x);
                            uiSize.y += field.SizeOfPanelUiMinusName().y;
                            uiPos.y += field.SizeOfPanelUiMinusName().y;
                        }
                    }
                    panel.size = uiSize + new Vector2(3 + largestDisplayname, 1);
                }

                for (int i = 0; i < data.fields.Length; i++)
                {
                    ManagedField field = data.fields[i];
                    DevUINode node = field.MakeAditionalNodes(data, this);
                    if (node != null)
                    {
                        subNodes.Add(node);
                        managedNodes[field.key] = node;
                    }
                }
            }
        }

        /// <summary>
        /// The panel spawned by <see cref="ManagedRepresentation"/> if any of its <see cref="ManagedField"/>s requires panel UI.
        /// Doesn't do much on its own besides keeping a white line that connects the panel and the placedobject representation.
        /// </summary>
        public class ManagedControlPanel : Panel
        {
            protected readonly ManagedRepresentation managedRepresentation;
            public Dictionary<string, DevUINode> managedNodes; // Added from ManagedRepresentation. Unused for now, but seems convenient for specialization
            public Dictionary<string, ManagedFieldWithPanel> managedFields; // Added from ManagedRepresentation. Unused for now, but seems convenient for specialization
            protected readonly int lineSprt;

            public ManagedControlPanel(DevUI owner, string IDstring, ManagedRepresentation parentNode, Vector2 pos, Vector2 size, string title) : base(owner, IDstring, parentNode, pos, size, title)
            {
                managedRepresentation = parentNode;
                managedNodes = new Dictionary<string, DevUINode>();
                managedFields = new Dictionary<string, ManagedFieldWithPanel>();

                fSprites.Add(new FSprite("pixel", true));
                owner.placedObjectsContainer.AddChild(fSprites[lineSprt = fSprites.Count - 1]);
                fSprites[lineSprt].anchorY = 0f;
            }
            public override void Refresh()
            {
                base.Refresh();
                Vector2 bottomLeft = collapsed ? nonCollapsedAbsPos + new Vector2(0f, size.y) : absPos;
                MoveSprite(lineSprt, bottomLeft);
                fSprites[lineSprt].scaleY = collapsed ? (pos + new Vector2(0f, size.y)).magnitude : pos.magnitude;
                fSprites[lineSprt].rotation = RWCustom.Custom.AimFromOneVectorToAnother(bottomLeft, (parentNode as PositionedDevUINode).absPos);
                (managedRepresentation.pObj.data as ManagedData).panelPos = pos; // mfw no "data with panel" intermediate class
            }
        }
        #endregion MANAGED

        #region FIELDS
        /// <summary>
        /// An interface for a <see cref="ManagedFieldWithPanel"/> that can be controlled through a <see cref="ManagedSlider"/>.
        /// </summary>
        public interface IInterpolablePanelField // sliders
        {
            float FactorOf(PositionedDevUINode node, ManagedData data);
            void NewFactor(PositionedDevUINode node, ManagedData data, float factor);
        }

        /// <summary>
        /// An interface for a <see cref="ManagedFieldWithPanel"/> that can be controlled through a <see cref="ManagedButton"/> or <see cref="ManagedArrowSelector"/>.
        /// </summary>
        public interface IIterablePanelField // buttons, arrows
        {
            void Next(PositionedDevUINode node, ManagedData data);
            void Prev(PositionedDevUINode node, ManagedData data);
        }

        public interface IListablePanelField
        {
            string[] list(PositionedDevUINode node, ManagedData data);

        }

        /// <summary>
        /// A <see cref="ManagedField"/> that stores a <see cref="float"/> value.
        /// </summary>
        public class FloatField : ManagedFieldWithPanel, IInterpolablePanelField, IIterablePanelField
        {
            protected readonly float min;
            protected readonly float max;
            protected readonly float increment;

            /// <summary>
            /// Creates a <see cref="ManagedField"/> that stores a <see cref="float"/>. Can be used as an Attribute for a field in your data class derived from <see cref="ManagedData"/>.
            /// </summary>
            /// <param name="key">The key to access that field with</param>
            /// <param name="min">the minimum allowed value</param>
            /// <param name="max">the maximum allowed value</param>
            /// <param name="defaultValue">the value a new data object is generated with</param>
            /// <param name="increment">controls digits when displaying the value, also behavior with buttons and arrows</param>
            /// <param name="control">the type of UI for this field</param>
            /// <param name="displayName">a display name for the panel, defaults to <paramref name="key"/></param>
            public FloatField(string key, float min, float max, float defaultValue, float increment = 0.1f, ControlType control = ControlType.slider, string displayName = null) : base(key, Mathf.Clamp(defaultValue, min, max), control, displayName)
            {
                this.min = Math.Min(min, max);
                this.max = Math.Max(min, max);
                this.increment = increment;
            }

            public override object FromString(string str)
            {
                return Mathf.Clamp(float.Parse(str), min, max);
            }

            protected virtual int NumberOfDecimals()
            {
                // fix decimals from https://stackoverflow.com/a/30205131
                decimal dec = new decimal(increment);
                dec = Math.Abs(dec); //make sure it is positive.
                dec -= (int)dec;     //remove the integer part of the number.
                int decimalPlaces = 0;
                while (dec > 0)
                {
                    decimalPlaces++;
                    dec *= 10;
                    dec -= (int)dec;
                }
                return decimalPlaces;
            }

            public override float SizeOfLargestDisplayValue()
            {
                return HUD.DialogBox.meanCharWidth * (Mathf.FloorToInt(Mathf.Max(Mathf.Abs(min), Mathf.Abs(max))).ToString().Length + 2 + NumberOfDecimals());
            }

            public override string DisplayValueForNode(PositionedDevUINode node, ManagedData data)
            {
                //return base.DisplayValueForNode(node, data);
                // fix too many decimals
                return data.GetValue<float>(key).ToString("N" + NumberOfDecimals());
            }

            /// <summary>
            /// Implements <see cref="IInterpolablePanelField"/>. Called from UI sliders.
            /// </summary>
            public virtual float FactorOf(PositionedDevUINode node, ManagedData data)
            {
                return max - min == 0 ? 0f : ((float)data.GetValue<float>(key) - min) / (max - min);
            }
            /// <summary>
            /// Implements <see cref="IInterpolablePanelField"/>. Called from UI sliders.
            /// </summary>
            public virtual void NewFactor(PositionedDevUINode node, ManagedData data, float factor)
            {
                data.SetValue(key, min + factor * (max - min));
            }
            /// <summary>
            /// Implements <see cref="IIterablePanelField"/>. Called from UI buttons and arrows.
            /// </summary>
            public virtual void Next(PositionedDevUINode node, ManagedData data)
            {
                float val = data.GetValue<float>(key) + increment;
                if (val > max) val = min;
                data.SetValue(key, val);
            }
            /// <summary>
            /// Implements <see cref="IIterablePanelField"/>. Called from UI buttons and arrows.
            /// </summary>
            public virtual void Prev(PositionedDevUINode node, ManagedData data)
            {
                float val = data.GetValue<float>(key) - increment;
                if (val < min) val = max;
                data.SetValue(key, val);
            }

            public override void ParseFromText(PositionedDevUINode node, ManagedData data, string newValue)
            {
                float val = float.Parse(newValue);
                if (val < min || val > max) throw new ArgumentException();
                base.ParseFromText(node, data, newValue);
            }
        }

        /// <summary>
        /// A <see cref="ManagedField"/> that stores a <see cref="bool"/> value.
        /// </summary>
        public class BooleanField : ManagedFieldWithPanel, IIterablePanelField, IInterpolablePanelField, IListablePanelField
        {
            /// <summary>
            /// Creates a <see cref="ManagedField"/> that stores a <see cref="bool"/>. Can be used as an Attribute for a field in your data class derived from <see cref="ManagedData"/>.
            /// </summary>
            /// <param name="key">The key to access that field with</param>
            /// <param name="defaultValue">the value a new data object is generated with</param>
            /// <param name="control">the type of UI for this field</param>
            /// <param name="displayName">a display name for the panel, defaults to <paramref name="key"/></param>
            public BooleanField(string key, bool defaultValue, ControlType control = ControlType.button, string displayName = null) : base(key, defaultValue, control, displayName)
            {
            }

            public override object FromString(string str)
            {
                return bool.Parse(str);
            }

            public override float SizeOfLargestDisplayValue()
            {
                return HUD.DialogBox.meanCharWidth * 5;
            }
            /// <summary>
            /// Implements <see cref="IInterpolablePanelField"/>. Called from UI sliders.
            /// </summary>
            public virtual float FactorOf(PositionedDevUINode node, ManagedData data)
            {
                return data.GetValue<bool>(key) ? 1f : 0f;
            }
            /// <summary>
            /// Implements <see cref="IInterpolablePanelField"/>. Called from UI sliders.
            /// </summary>
            public virtual void NewFactor(PositionedDevUINode node, ManagedData data, float factor)
            {
                data.SetValue(key, factor > 0.5f);
            }
            /// <summary>
            /// Implements <see cref="IIterablePanelField"/>. Called from UI buttons and arrows.
            /// </summary>
            public virtual void Next(PositionedDevUINode node, ManagedData data)
            {
                data.SetValue(key, !data.GetValue<bool>(key));
            }
            /// <summary>
            /// Implements <see cref="IIterablePanelField"/>. Called from UI buttons and arrows.
            /// </summary>
            public virtual void Prev(PositionedDevUINode node, ManagedData data)
            {
                data.SetValue(key, !data.GetValue<bool>(key));
            }
            /// <summary>
            /// Implements <see cref="IListablePanelField"/>. Called from UI buttons and arrows.
            /// </summary>
            public string[] list(PositionedDevUINode node, ManagedData data)
            {
                return new string[] { "False", "True"};
            }
        }

        /// <summary>
        /// A <see cref="ManagedField"/> that stores an <see cref="Enum"/> value.
        /// </summary>
        public class EnumField : ManagedFieldWithPanel, IIterablePanelField, IInterpolablePanelField, IListablePanelField
        {
            protected readonly Type type;
            protected Enum[] _possibleValues;
            /// <summary>
            /// Creates a <see cref="ManagedField"/> that stores an <see cref="Enum"/> of the specified type.
            /// Cannot be used as Attribute, instead you should pass this object to <see cref="ManagedData(PlacedObject, ManagedField[])"/> and mark your field with the <see cref="ManagedData.BackedByField"/> attribute.
            /// </summary>
            /// <param name="key">The key to access that field with</param>
            /// <param name="type">the enum type this field is for</param>
            /// <param name="defaultValue">the value a new data object is generated with</param>
            /// <param name="possibleValues">the acceptable values for this field, defaults to a deferred call to <see cref="Enum.GetValues"/> for the type</param>
            /// <param name="control">the type of UI for this field</param>
            /// <param name="displayName">a display name for the panel, defaults to <paramref name="key"/></param>
            public EnumField(string key, Type type, Enum defaultValue, Enum[] possibleValues = null, ControlType control = ControlType.arrows, string displayName = null) : base(key, possibleValues != null && !possibleValues.Contains(defaultValue) ? possibleValues[0] : defaultValue, control, displayName)
            {
                this.type = type;
                _possibleValues = possibleValues;
            }

            protected virtual Enum[] PossibleValues // We defer this listing so enumextend can do its magic.
            {
                get
                {
                    if (_possibleValues == null) _possibleValues = Enum.GetValues(type).Cast<Enum>().ToArray();
                    return _possibleValues;
                }
            }

            public override object FromString(string str)
            {
                Enum fromstring = (Enum)Enum.Parse(type, str);
                return PossibleValues.Contains(fromstring) ? fromstring : PossibleValues[0];
            }

            public override float SizeOfLargestDisplayValue()
            {
                int longestEnum = PossibleValues.Aggregate(0, (longest, next) =>
                        next.ToString().Length > longest ? next.ToString().Length : longest);
                return HUD.DialogBox.meanCharWidth * longestEnum + 2;
            }
            /// <summary>
            /// Implements <see cref="IInterpolablePanelField"/>. Called from UI sliders.
            /// </summary>
            public virtual float FactorOf(PositionedDevUINode node, ManagedData data)
            {
                return Array.IndexOf(PossibleValues, data.GetValue<Enum>(key)) / (float)(PossibleValues.Length - 1);
            }
            /// <summary>
            /// Implements <see cref="IInterpolablePanelField"/>. Called from UI sliders.
            /// </summary>
            public virtual void NewFactor(PositionedDevUINode node, ManagedData data, float factor)
            {
                data.SetValue(key, PossibleValues[Mathf.RoundToInt(factor * (PossibleValues.Length - 1))]);
            }
            /// <summary>
            /// Implements <see cref="IIterablePanelField"/>. Called from UI buttons and arrows.
            /// </summary>
            public virtual void Next(PositionedDevUINode node, ManagedData data)
            {
                data.SetValue(key, PossibleValues[(Array.IndexOf(PossibleValues, data.GetValue<Enum>(key)) + 1) % PossibleValues.Length]);
            }
            /// <summary>
            /// Implements <see cref="IIterablePanelField"/>. Called from UI buttons and arrows.
            /// </summary>
            public virtual void Prev(PositionedDevUINode node, ManagedData data)
            {
                data.SetValue(key, PossibleValues[(Array.IndexOf(PossibleValues, data.GetValue<Enum>(key)) - 1 + PossibleValues.Length) % PossibleValues.Length]);
            }

            public override void ParseFromText(PositionedDevUINode node, ManagedData data, string newValue)
            {
                Enum fromstring;
                try
                {
                    fromstring = (Enum)Enum.Parse(type, newValue);
                }
                catch (Exception)
                {
                    foreach (Enum val in PossibleValues)
                    {
                        if (val.ToString().ToLowerInvariant() == newValue.ToLowerInvariant())
                        {
                            // This check is flawed if we have for instance "aa" and "AAA" and "aaa" it becomes impossible to type in the second one
                            // But honestly who would name their enums like that...
                            data.SetValue(key, val);
                            return;
                        }
                    }
                    throw;
                }
                if (!PossibleValues.Contains(fromstring)) throw new ArgumentException();

                data.SetValue(key, fromstring);
                //base.ParseFromText(node, data, newValue);
            }

            public string[] list(PositionedDevUINode node, ManagedData data)
            {
                string[] result = new string[PossibleValues.Length];
                for (int i = 0; i < PossibleValues.Length; i++)
                {
                    result[i] = PossibleValues[i].ToString();
                }
                return result;
            }
        }

        /// <summary>
        /// A <see cref="ManagedField"/> that stores an <see cref="int"/> value.
        /// </summary>
        public class IntegerField : ManagedFieldWithPanel, IIterablePanelField, IInterpolablePanelField, IListablePanelField
        {
            protected readonly int min;
            protected readonly int max;
            /// <summary>
            /// Creates a <see cref="ManagedField"/> that stores an <see cref="int"/>. Can be used as an Attribute for a field in your data class derived from <see cref="ManagedData"/>.
            /// </summary>
            /// <param name="key">The key to access that field with</param>
            /// <param name="min">the minimum allowed value (inclusive)</param>
            /// <param name="max">the maximum allowed value (inclusive)</param>
            /// <param name="defaultValue">the value a new data object is generated with</param>
            /// <param name="control">the type of UI for this field</param>
            /// <param name="displayName">a display name for the panel, defaults to <paramref name="key"/></param>
            public IntegerField(string key, int min, int max, int defaultValue, ControlType control = ControlType.arrows, string displayName = null) : base(key, Mathf.Clamp(defaultValue, min, max), control, displayName)
            {
                this.min = Math.Min(min, max); // trust nobody
                this.max = Math.Max(min, max);
            }

            public override object FromString(string str)
            {
                return Mathf.Clamp(int.Parse(str), min, max);
            }

            public override float SizeOfLargestDisplayValue()
            {
                return HUD.DialogBox.meanCharWidth * (Mathf.Max(Mathf.Abs(min), Mathf.Abs(max)).ToString().Length + 2);
            }
            /// <summary>
            /// Implements <see cref="IInterpolablePanelField"/>. Called from UI sliders.
            /// </summary>
            public virtual float FactorOf(PositionedDevUINode node, ManagedData data)
            {
                return max - min == 0 ? 0f : (data.GetValue<int>(key) - min) / (float)(max - min);
            }
            /// <summary>
            /// Implements <see cref="IInterpolablePanelField"/>. Called from UI sliders.
            /// </summary>
            public virtual void NewFactor(PositionedDevUINode node, ManagedData data, float factor)
            {
                data.SetValue(key, Mathf.RoundToInt(min + factor * (max - min)));
            }
            /// <summary>
            /// Implements <see cref="IIterablePanelField"/>. Called from UI buttons and arrows.
            /// </summary>
            public virtual void Next(PositionedDevUINode node, ManagedData data)
            {
                int val = data.GetValue<int>(key) + 1;
                if (val > max) val = min;
                data.SetValue(key, val);
            }
            /// <summary>
            /// Implements <see cref="IIterablePanelField"/>. Called from UI buttons and arrows.
            /// </summary>
            public virtual void Prev(PositionedDevUINode node, ManagedData data)
            {
                int val = data.GetValue<int>(key) - 1;
                if (val < min) val = max;
                data.SetValue(key, val);
            }

            public override void ParseFromText(PositionedDevUINode node, ManagedData data, string newValue)
            {
                int val = int.Parse(newValue);
                if (val < min || val > max) throw new ArgumentException();
                base.ParseFromText(node, data, newValue);
            }

            public string[] list(PositionedDevUINode node, ManagedData data)
            {
                int values = max - min + 1;
                string[] result = new string[values];
                for (int i = 0; i < values; i++)
                {
                    result[i] = (min + i).ToString();
                }

                return result;
            }
        }

        /// <summary>
        /// A <see cref="ManagedField"/> that stores a <see cref="string"/> value.
        /// </summary>
        public class StringField : ManagedFieldWithPanel
        {
            /// <summary>
            /// Creates a <see cref="ManagedField"/> that stores a <see cref="string"/>. Can be used as an Attribute for a field in your data class derived from <see cref="ManagedData"/>.
            /// </summary>
            /// <param name="key">The key to access that field with</param>
            /// <param name="defaultValue">the value a new data object is generated with</param>
            /// <param name="displayName">a display name for the panel, defaults to <paramref name="key"/></param>
            public StringField(string key, string defaultValue, string displayName = null) : base(key, defaultValue, ControlType.text, displayName)
            {

            }

            protected readonly static Dictionary<string, string> replacements = new Dictionary<string, string>
            {
                { ": ","%1" },
                { ", ","%2" },
                { "><","%3" },
                { "~","%4" },
                { "%","%0" }, // this goes last, very important
            };

            public override object FromString(string str)
            {
                //return str[0];
                return replacements.Aggregate(str, (current, value) =>
                    current.Replace(value.Value, value.Key));
            }

            public override string ToString(object value)
            {
                //return new string[] { value.ToString() };
                return replacements.Reverse().Aggregate(value.ToString(), (current, val) =>
                    current.Replace(val.Key, val.Value));
            }

            public override float SizeOfLargestDisplayValue()
            {
                return HUD.DialogBox.meanCharWidth * 25; // No character limit but this is the expected reasonable max anyone would be using ?
            }

            public override string DisplayValueForNode(PositionedDevUINode node, ManagedData data) // bypass replacements
            {
                return data.GetValue<string>(key);
            }

            public override void ParseFromText(PositionedDevUINode node, ManagedData data, string newValue) // no replacements
            {
                data.SetValue(key, newValue);
            }
        }

        /// <summary>
        /// A <see cref="ManagedField"/> for a <see cref="Vector2"/> value.
        /// </summary>
        public class Vector2Field : ManagedField
        {
            protected readonly VectorReprType controlType;
            protected readonly string label;

            /// <summary>
            /// Creates a <see cref="ManagedField"/> that stores a <see cref="Vector2"/>.
            /// Cannot be used as Attribute, instead you should pass this object to <see cref="ManagedData(PlacedObject, ManagedField[])"/> and mark your field with the <see cref="ManagedData.BackedByField"/> attribute.
            /// </summary>
            /// <param name="key">The key to access that field with</param>
            /// <param name="defaultValue">the value a new data object is generated with</param>
            /// <param name="controlType">the type of UI for this field, from <see cref="VectorReprType"/></param>
            public Vector2Field(string key, Vector2 defaultValue, VectorReprType controlType = VectorReprType.line, string label = null) : base(key, defaultValue)
            {
                this.controlType = controlType;
                this.label = label ?? "";
            }

            public Vector2Field(string key, float defX, float defY, VectorReprType ct = VectorReprType.line, string label = null)
                : this(key, new Vector2(defX, defY), ct, label)
            {

            }
            public enum VectorReprType
            {
                none,
                line,
                circle,
                rect,
            }

            public override object FromString(string str)
            {
                string[] arr = Regex.Split(str, "\\^");
                return new Vector2(float.Parse(arr[0]), float.Parse(arr[1]));
            }

            public override string ToString(object value)
            {
                Vector2 vec = (Vector2)value;
                return string.Join("^", new string[] { vec.x.ToString(), vec.y.ToString() });
            }

            public override DevUINode MakeAditionalNodes(ManagedData managedData, ManagedRepresentation managedRepresentation)
            {
                return new ManagedVectorHandle(this, managedData, managedRepresentation, controlType);
            }
        }

        /// <summary>
        /// A <see cref="ManagedField"/> for a <see cref="RWCustom.IntVector2"/> value.
        /// </summary>
        public class IntVector2Field : ManagedField
        {
            protected readonly IntVectorReprType controlType;
            /// <summary>
            /// Creates a <see cref="ManagedField"/> that stores a <see cref="RWCustom.IntVector2"/>.
            /// Cannot be used as Attribute, instead you should pass this object to <see cref="ManagedData(PlacedObject, ManagedField[])"/> and mark your field with the <see cref="ManagedData.BackedByField"/> attribute.
            /// </summary>
            /// <param name="key">The key to access that field with</param>
            /// <param name="defaultValue">the value a new data object is generated with</param>
            /// <param name="controlType">the type of UI for this field, from <see cref="IntVectorReprType"/></param>
            public IntVector2Field(string key, RWCustom.IntVector2 defaultValue, IntVectorReprType controlType = IntVectorReprType.line) : base(key, defaultValue)
            {
                this.controlType = controlType;
            }
            public IntVector2Field(string key, int defX, int defY, IntVectorReprType ct = IntVectorReprType.line)
                : this(key, new RWCustom.IntVector2(defX, defY), ct)
            {

            }

            public enum IntVectorReprType
            {
                none,
                line,
                tile,
                fourdir,
                eightdir,
                rect,
            }

            public override object FromString(string str)
            {
                string[] arr = Regex.Split(str, "\\^");
                return new RWCustom.IntVector2(int.Parse(arr[0]), int.Parse(arr[1]));
            }

            public override string ToString(object value)
            {
                RWCustom.IntVector2 vec = (RWCustom.IntVector2)value;
                return string.Join("^", new string[] { vec.x.ToString(), vec.y.ToString() });
            }

            public override DevUINode MakeAditionalNodes(ManagedData managedData, ManagedRepresentation managedRepresentation)
            {
                return new ManagedIntHandle(this, managedData, managedRepresentation, controlType);
            }
        }

        /// <summary>
        /// A <see cref="ManagedField"/> for a <see cref="Color"/> value.
        /// </summary>
        public class ColorField : ManagedFieldWithPanel, IInterpolablePanelField
        {
            /// <summary>
            /// Creates a <see cref="ManagedField"/> that stores a <see cref="Color"/>.
            /// Cannot be used as Attribute, instead you should pass this object to <see cref="ManagedData(PlacedObject, ManagedField[])"/> and mark your field with the <see cref="ManagedData.BackedByField"/> attribute.
            /// </summary>
            /// <param name="key">The key to access that field with</param>
            /// <param name="defaultColor">the value a new data object is generated with</param>
            /// <param name="controlType">one of <see cref="ManagedFieldWithPanel.ControlType.text"/> or <see cref="ManagedFieldWithPanel.ControlType.slider"/></param>
            public ColorField(string key, Color defaultColor, ControlType controlType = ControlType.text, string displayName = null) : base(key, defaultColor, controlType, displayName) { }
            public ColorField(string key, float defR, float defG, float defB,
                float defA = 1f, ControlType ct = ControlType.text, string DisplayName = null)
                : this(key, new Color(defR, defG, defB, defA), ct, DisplayName) { }

            public override object FromString(string str)
            {
                return new Color(
                        Convert.ToInt32(str.Substring(0, 2), 16) / 255f,
                        Convert.ToInt32(str.Substring(2, 2), 16) / 255f,
                        Convert.ToInt32(str.Substring(4, 2), 16) / 255f);
            }

            public override string ToString(object value)
            {
                Color color = (Color)value;
                return string.Join("", new string[] {Mathf.RoundToInt(color.r * 255).ToString("X2"),
                    Mathf.RoundToInt(color.g * 255).ToString("X2"),
                    Mathf.RoundToInt(color.b * 255).ToString("X2")});
            }

            public override void ParseFromText(PositionedDevUINode node, ManagedData data, string newValue)
            {
                if (newValue.StartsWith("#")) newValue = newValue.Substring(1);
                if (newValue.Length != 6) throw new ArgumentException();
                data.SetValue(key, FromString(newValue));
            }

            public override string DisplayValueForNode(PositionedDevUINode node, ManagedData data)
            {
                switch (control)
                {
                    case ControlType.slider:
                        Color color = data.GetValue<Color>(key);
                        ColorSliderControl control = node.parentNode as ColorSliderControl;
                        if (node == control.rslider) return Mathf.RoundToInt(color.r * 255).ToString();
                        if (node == control.gslider) return Mathf.RoundToInt(color.g * 255).ToString();
                        return Mathf.RoundToInt(color.b * 255).ToString();
                    case ControlType.text:
                        return "#" + ToString(data.GetValue<object>(key));
                    default:
                        return null;
                }
            }
            public override float SizeOfLargestDisplayValue()
            {
                switch (control)
                {
                    case ControlType.slider:
                        return HUD.DialogBox.meanCharWidth * 4; ;
                    case ControlType.text:
                        return HUD.DialogBox.meanCharWidth * 9; ;
                    default:
                        return 0;
                }
            }

            public override float SizeOfDisplayname()
            {
                switch (control)
                {
                    case ControlType.slider:
                        return HUD.DialogBox.meanCharWidth * (displayName.Length + 6);
                    default:
                        return base.SizeOfDisplayname();
                }
            }

            public override Vector2 SizeOfPanelUiMinusName()
            {
                switch (control)
                {
                    case ControlType.slider:
                        Vector2 size = base.SizeOfPanelUiMinusName();
                        size.y = 60;
                        return size;
                    default:
                        return base.SizeOfPanelUiMinusName();
                }

            }

            public override PositionedDevUINode MakeControlPanelNode(ManagedData managedData, ManagedControlPanel panel, float sizeOfDisplayname)
            {
                switch (control)
                {
                    case ControlType.slider:
                        return new ColorSliderControl(this, managedData, panel, sizeOfDisplayname);
                    case ControlType.text:
                        return base.MakeControlPanelNode(managedData, panel, sizeOfDisplayname);
                    case ControlType.arrows:
                    case ControlType.button:
                        throw new NotImplementedException();
                    default:
                        break;
                }
                return null;
            }
            public float FactorOf(PositionedDevUINode node, ManagedData data)
            {
                Color color = data.GetValue<Color>(key);
                ColorSliderControl control = node.parentNode as ColorSliderControl;
                if (node == control.rslider) return color.r;
                if (node == control.gslider) return color.g;
                return color.b;

            }

            public void NewFactor(PositionedDevUINode node, ManagedData data, float factor)
            {
                Color color = data.GetValue<Color>(key);
                ColorSliderControl control = node.parentNode as ColorSliderControl;
                if (node == control.rslider) color.r = factor;
                else if (node == control.gslider) color.g = factor;
                else color.b = factor;
                data.SetValue(key, color);
            }

            private class ColorSliderControl : PositionedDevUINode
            {
                public ManagedSlider rslider;
                public ManagedSlider gslider;
                public ManagedSlider bslider;

                public ColorSliderControl(ColorField field, ManagedData managedData, DevUINode parent, float sizeOfDisplayname) : base(parent.owner, field.key, parent, Vector2.zero)
                {
                    rslider = new ManagedSlider(field, managedData, this, sizeOfDisplayname);
                    rslider.pos = new Vector2(0, 40);
                    (rslider.subNodes[0] as DevUILabel).Text += " - R";
                    subNodes.Add(rslider);
                    gslider = new ManagedSlider(field, managedData, this, sizeOfDisplayname);
                    (gslider.subNodes[0] as DevUILabel).Text += " - G";
                    gslider.pos = new Vector2(0, 20);
                    subNodes.Add(gslider);
                    bslider = new ManagedSlider(field, managedData, this, sizeOfDisplayname);
                    (bslider.subNodes[0] as DevUILabel).Text += " - B";
                    subNodes.Add(bslider);
                }
            }
        }

        public class DrivenVector2Field : Vector2Field
        {
            private readonly string keyOfOther;
            private readonly DrivenControlType drivenControlType;

            public enum DrivenControlType
            {
                relativeLine,
                perpendicularLine,
                perpendicularOval,
                rectangle,
            }

            public DrivenVector2Field(string keyofSelf, string keyOfOther, Vector2 defaultValue, DrivenControlType controlType = DrivenControlType.perpendicularLine, string label = null) : base(keyofSelf, defaultValue, VectorReprType.none, label)
            {
                this.keyOfOther = keyOfOther;
                drivenControlType = controlType;
            }

            public override DevUINode MakeAditionalNodes(ManagedData managedData, ManagedRepresentation managedRepresentation)
            {
                switch (drivenControlType)
                {
                    case DrivenControlType.relativeLine:
                        return new DrivenVectorControl(this, managedData, managedRepresentation.managedNodes[keyOfOther] as PositionedDevUINode, drivenControlType, label);
                    case DrivenControlType.perpendicularLine:
                    case DrivenControlType.perpendicularOval:
                    case DrivenControlType.rectangle:
                        return new DrivenVectorControl(this, managedData, managedRepresentation, drivenControlType, label);
                    default:
                        return null;
                }
            }

            public class DrivenVectorControl : PositionedDevUINode
            {
                protected readonly DrivenVector2Field control;
                protected readonly ManagedData data;
                protected readonly DrivenControlType controlType;
                protected Handle handleB;
                protected FSprite circleSprite;
                protected FSprite lineBSprite;
                private int[] rect;

                public DrivenVectorControl(DrivenVector2Field control, ManagedData data, PositionedDevUINode repr, DrivenControlType controlType, string label) : base(repr.owner, control.key, repr, Vector2.zero)
                {
                    this.control = control;
                    this.data = data;
                    this.controlType = controlType;

                    handleB = new Handle(owner, "V_Handle", this, new Vector2(100f, 0f));
                    handleB.subNodes.Add(new DevUILabel(owner, "hbl", handleB, new Vector2(-3.5f, -7.5f), 16, label) { spriteColor = Color.clear });
                    subNodes.Add(handleB);

                    handleB.pos = data.GetValue<Vector2>(control.key);

                    switch (controlType)
                    {
                        case DrivenControlType.perpendicularLine:
                        case DrivenControlType.relativeLine:
                            fSprites.Add(lineBSprite = new FSprite("pixel", true) { anchorY = 0f });
                            owner.placedObjectsContainer.AddChild(lineBSprite);
                            break;
                        case DrivenControlType.perpendicularOval:
                            fSprites.Add(circleSprite = new FSprite("Futile_White", true)
                            {
                                shader = owner.room.game.rainWorld.Shaders["VectorCircle"],
                                alpha = 0.02f
                            });
                            owner.placedObjectsContainer.AddChild(circleSprite);
                            break;
                        case DrivenControlType.rectangle:
                            rect = new int[5];
                            for (int i = 0; i < 5; i++)
                            {
                                rect[i] = fSprites.Count;
                                fSprites.Add(new FSprite("pixel", true));
                                owner.placedObjectsContainer.AddChild(fSprites[rect[i]]);
                                fSprites[rect[i]].anchorX = 0f;
                                fSprites[rect[i]].anchorY = 0f;
                            }
                            fSprites[rect[4]].alpha = 0.05f;
                            break;

                        default:
                            break;
                    }
                }

                public override void Refresh()
                {
                    base.Refresh();
                    Vector2 drivingPos = data.GetValue<Vector2>(control.keyOfOther);
                    switch (controlType)
                    {
                        case DrivenControlType.relativeLine:
                            // ??? nothing to do here
                            break;
                        case DrivenControlType.perpendicularLine:
                        case DrivenControlType.perpendicularOval:
                        case DrivenControlType.rectangle:
                            Vector2 perp = RWCustom.Custom.PerpendicularVector(drivingPos);
                            handleB.pos = perp * handleB.pos.magnitude;// * handleB.pos.magnitude;
                            break;
                    }
                    switch (controlType)
                    {
                        case DrivenControlType.perpendicularLine:
                        case DrivenControlType.relativeLine:
                            lineBSprite.SetPosition(absPos);
                            lineBSprite.scaleY = handleB.pos.magnitude;
                            lineBSprite.rotation = RWCustom.Custom.VecToDeg(handleB.pos);
                            break;
                        case DrivenControlType.perpendicularOval:
                            circleSprite.SetPosition(absPos);
                            circleSprite.scaleY = drivingPos.magnitude / 8f;
                            circleSprite.scaleX = handleB.pos.magnitude / 8f;
                            circleSprite.rotation = RWCustom.Custom.VecToDeg(drivingPos);
                            break;
                        case DrivenControlType.rectangle:
                            Vector2 leftbottom;// = Vector2.zero;
                            Vector2 topright;// = Vector2.zero;
                            Vector2 bottomright;
                            Vector2 topleft;

                            leftbottom = (parentNode as PositionedDevUINode).absPos;
                            bottomright = leftbottom + drivingPos;
                            topleft = handleB.absPos;
                            topright = leftbottom + drivingPos + handleB.pos;//absPos;
                                                                             //Vector2 size = (topright - leftbottom);

                            MoveSprite(rect[0], leftbottom);
                            fSprites[rect[0]].scaleY = drivingPos.magnitude;
                            fSprites[rect[0]].rotation = RWCustom.Custom.VecToDeg(drivingPos);
                            MoveSprite(rect[1], leftbottom);
                            fSprites[rect[1]].scaleY = handleB.pos.magnitude;
                            fSprites[rect[1]].rotation = RWCustom.Custom.VecToDeg(handleB.pos);
                            MoveSprite(rect[2], topright);
                            fSprites[rect[2]].scaleY = drivingPos.magnitude;
                            fSprites[rect[2]].rotation = RWCustom.Custom.VecToDeg(drivingPos) + 180f;
                            MoveSprite(rect[3], topright);
                            fSprites[rect[3]].scaleY = handleB.pos.magnitude;
                            fSprites[rect[3]].rotation = RWCustom.Custom.VecToDeg(handleB.pos) + 180f;
                            MoveSprite(rect[4], leftbottom);
                            fSprites[rect[4]].scaleX = drivingPos.magnitude;
                            fSprites[rect[4]].scaleY = handleB.pos.magnitude;
                            fSprites[rect[4]].rotation = RWCustom.Custom.VecToDeg(handleB.pos);
                            break;
                        default:
                            break;
                    }
                    data.SetValue(control.key, handleB.pos);
                }
            }
        }


        #endregion FIELDS

        #region CONTROLS

        // An undocumented mess. Have a look around, find what suits you, maybe implement your own.
        // in commit d2dad8768371565bb9b538263ac0b0ac595913b7 there was a slider-button used for text before text input became a thing
        // an arrows-text combo would also be amazing for enums and ints wink wink

        public class ManagedVectorHandle : Handle // All-in-one super handle
        {
            protected readonly Vector2Field field;
            protected readonly ManagedData data;
            protected readonly Vector2Field.VectorReprType reprType;
            protected readonly int line = -1;
            protected readonly int circle = -1;
            protected readonly int[] rect;

            public ManagedVectorHandle(Vector2Field field, ManagedData managedData, ManagedRepresentation repr, Vector2Field.VectorReprType reprType) : base(repr.owner, field.key, repr, managedData.GetValue<Vector2>(field.key))
            {
                this.field = field;
                data = managedData;
                this.reprType = reprType;
                switch (reprType)
                {
                    case Vector2Field.VectorReprType.circle:
                    case Vector2Field.VectorReprType.line:
                        line = fSprites.Count;
                        fSprites.Add(new FSprite("pixel", true));
                        owner.placedObjectsContainer.AddChild(fSprites[line]);
                        fSprites[line].anchorY = 0;
                        if (reprType != Vector2Field.VectorReprType.circle)
                            break;
                        //case Vector2Field.VectorReprType.circle:
                        circle = fSprites.Count;
                        fSprites.Add(new FSprite("Futile_White", true));
                        owner.placedObjectsContainer.AddChild(fSprites[circle]);
                        fSprites[circle].shader = owner.room.game.rainWorld.Shaders["VectorCircle"];
                        break;
                    case Vector2Field.VectorReprType.rect:
                        rect = new int[5];
                        for (int i = 0; i < 5; i++)
                        {
                            rect[i] = fSprites.Count;
                            fSprites.Add(new FSprite("pixel", true));
                            owner.placedObjectsContainer.AddChild(fSprites[rect[i]]);
                            fSprites[rect[i]].anchorX = 0f;
                            fSprites[rect[i]].anchorY = 0f;
                        }
                        fSprites[rect[4]].alpha = 0.05f;
                        break;
                    default:
                        break;
                }
            }

            public override void Move(Vector2 newPos)
            {
                data.SetValue(field.key, newPos);
                base.Move(newPos); // calls refresh
            }

            public override void Refresh()
            {
                base.Refresh();
                pos = data.GetValue<Vector2>(field.key);

                if (line >= 0)
                {
                    MoveSprite(line, absPos);
                    fSprites[line].scaleY = pos.magnitude;
                    //this.fSprites[line].rotation = RWCustom.Custom.AimFromOneVectorToAnother(this.absPos, (parentNode as PositionedDevUINode).absPos); // but why
                    fSprites[line].rotation = RWCustom.Custom.VecToDeg(-pos);
                }
                if (circle >= 0)
                {
                    MoveSprite(circle, (parentNode as PositionedDevUINode).absPos);
                    fSprites[circle].scale = pos.magnitude / 8f;
                    fSprites[circle].alpha = 2f / pos.magnitude;
                }
                if (rect != null)
                {

                    Vector2 leftbottom = Vector2.zero;
                    Vector2 topright = Vector2.zero;
                    // rectgrid abandoned

                    leftbottom = (parentNode as PositionedDevUINode).absPos + leftbottom;
                    topright = absPos + topright;
                    Vector2 size = topright - leftbottom;

                    MoveSprite(rect[0], leftbottom);
                    fSprites[rect[0]].scaleY = size.y;// + size.y.Sign();
                    MoveSprite(rect[1], leftbottom);
                    fSprites[rect[1]].scaleX = size.x;// + size.x.Sign();
                    MoveSprite(rect[2], topright);
                    fSprites[rect[2]].scaleY = -size.y;// - size.y.Sign();
                    MoveSprite(rect[3], topright);
                    fSprites[rect[3]].scaleX = -size.x;// - size.x.Sign();
                    MoveSprite(rect[4], leftbottom);
                    fSprites[rect[4]].scaleX = size.x;// + size.x.Sign();
                    fSprites[rect[4]].scaleY = size.y;// + size.y.Sign();
                }
            }

            public override void SetColor(Color col)
            {
                base.SetColor(col);
                if (line >= 0)
                {
                    fSprites[line].color = col;
                }
                if (circle >= 0)
                {
                    fSprites[circle].color = col;
                }

                if (rect != null)
                {
                    for (int i = 0; i < rect.Length; i++)
                    {
                        fSprites[rect[i]].color = col;
                    }
                }
            }
        }

        public class ManagedIntHandle : Handle // All-in-one super handle 2
        {
            protected readonly IntVector2Field field;
            protected readonly ManagedData data;
            protected readonly IntVector2Field.IntVectorReprType reprType;
            protected readonly int pixel = -1;
            protected readonly int[] rect;

            public ManagedIntHandle(IntVector2Field field, ManagedData managedData, ManagedRepresentation repr, IntVector2Field.IntVectorReprType reprType) : base(repr.owner, field.key, repr, managedData.GetValue<RWCustom.IntVector2>(field.key).ToVector2() * 20f)
            {
                this.field = field;
                data = managedData;
                this.reprType = reprType;
                switch (reprType)
                {
                    case IntVector2Field.IntVectorReprType.line:
                    case IntVector2Field.IntVectorReprType.tile:
                    case IntVector2Field.IntVectorReprType.fourdir:
                    case IntVector2Field.IntVectorReprType.eightdir:
                        pixel = fSprites.Count;
                        fSprites.Add(new FSprite("pixel", true));
                        owner.placedObjectsContainer.AddChild(fSprites[pixel]);
                        fSprites[pixel].MoveBehindOtherNode(fSprites[0]); // attention to detail

                        if (reprType == IntVector2Field.IntVectorReprType.tile)
                        {
                            fSprites[pixel].alpha = 0.25f;
                            fSprites[pixel].scale = 20f;
                        }

                        fSprites[pixel].anchorX = 0;
                        fSprites[pixel].anchorY = 0;

                        if (reprType == IntVector2Field.IntVectorReprType.fourdir || reprType == IntVector2Field.IntVectorReprType.eightdir)
                        {
                            fSprites[0].SetElementByName("Menu_Symbol_Arrow");
                            fSprites[0].scale = 0.75f;
                        }

                        break;
                    case IntVector2Field.IntVectorReprType.rect:
                        rect = new int[5];
                        for (int i = 0; i < 5; i++)
                        {
                            rect[i] = fSprites.Count;
                            fSprites.Add(new FSprite("pixel", true));
                            owner.placedObjectsContainer.AddChild(fSprites[rect[i]]);
                            fSprites[rect[i]].anchorX = 0f;
                            fSprites[rect[i]].anchorY = 0f;
                        }
                        fSprites[rect[4]].alpha = 0.05f;
                        break;
                    default:
                        break;
                }
            }

            public override void Move(Vector2 newPos)
            {
                // absolute so we're aligned with room tiles
                if (reprType == IntVector2Field.IntVectorReprType.fourdir)
                {
                    float dir = RWCustom.Custom.VecToDeg(newPos);
                    dir -= (dir + 45f + 360f) % 90f - 45f;
                    newPos = RWCustom.Custom.DegToVec(dir) * 20f;
                }
                else if (reprType == IntVector2Field.IntVectorReprType.eightdir)
                {
                    float dir = RWCustom.Custom.VecToDeg(newPos);
                    dir -= (dir + 22.5f + 360f) % 45f - 22.5f;
                    newPos = RWCustom.Custom.DegToVec(dir);
                    if (Mathf.Abs(newPos.x) > 0.5f) newPos.x = Mathf.Sign(newPos.x);
                    if (Mathf.Abs(newPos.y) > 0.5f) newPos.y = Mathf.Sign(newPos.y);
                    newPos *= 20f;
                }
                Vector2 parentPos = (parentNode as PositionedDevUINode).pos + owner.game.cameras[0].pos;
                Vector2 roompos = newPos + parentPos;
                RWCustom.IntVector2 ownIntPos = new RWCustom.IntVector2(Mathf.FloorToInt(roompos.x / 20f), Mathf.FloorToInt(roompos.y / 20f));
                RWCustom.IntVector2 parentIntPos = new RWCustom.IntVector2(Mathf.FloorToInt(parentPos.x / 20f), Mathf.FloorToInt(parentPos.y / 20f));
                // relativize again
                ownIntPos -= parentIntPos;
                newPos = ownIntPos.ToVector2() * 20f;

                //Vector2 roompos = newPos;
                //RWCustom.IntVector2 ownIntPos = new RWCustom.IntVector2(Mathf.FloorToInt(roompos.x / 20f), Mathf.FloorToInt(roompos.y / 20f));
                //newPos = ownIntPos.ToVector2() * 20f;

                data.SetValue(field.key, ownIntPos);
                base.Move(newPos); // calls refresh
            }

            public override void Refresh()
            {
                base.Refresh();
                pos = data.GetValue<RWCustom.IntVector2>(field.key).ToVector2() * 20f;

                if (pixel >= 0)
                {
                    switch (reprType)
                    {

                        case IntVector2Field.IntVectorReprType.tile:
                            MoveSprite(pixel, new Vector2(Mathf.FloorToInt(absPos.x / 20f), Mathf.FloorToInt(absPos.y / 20f)) * 20f);
                            break;
                        case IntVector2Field.IntVectorReprType.line:
                        case IntVector2Field.IntVectorReprType.fourdir:
                        case IntVector2Field.IntVectorReprType.eightdir:
                            if (reprType == IntVector2Field.IntVectorReprType.fourdir || reprType == IntVector2Field.IntVectorReprType.eightdir)
                                fSprites[0].rotation = RWCustom.Custom.VecToDeg(pos);

                            MoveSprite(pixel, absPos);
                            fSprites[pixel].scaleY = pos.magnitude;
                            fSprites[pixel].rotation = RWCustom.Custom.VecToDeg(-pos);
                            break;
                    }
                }

                if (rect != null)
                {
                    Vector2 parentPos = (parentNode as PositionedDevUINode).pos + owner.game.cameras[0].pos;
                    Vector2 roompos = pos + parentPos;
                    Vector2 offset = -owner.game.cameras[0].pos;
                    RWCustom.IntVector2 ownIntPos = new RWCustom.IntVector2(Mathf.FloorToInt(roompos.x / 20f), Mathf.FloorToInt(roompos.y / 20f));
                    RWCustom.IntVector2 parentIntPos = new RWCustom.IntVector2(Mathf.FloorToInt(parentPos.x / 20f), Mathf.FloorToInt(parentPos.y / 20f));

                    Vector2 leftbottom = offset + new Vector2(Mathf.Min(ownIntPos.x, parentIntPos.x) * 20f, Mathf.Min(ownIntPos.y, parentIntPos.y) * 20f);
                    Vector2 topright = offset + new Vector2(Mathf.Max(ownIntPos.x, parentIntPos.x) * 20f + 20f, Mathf.Max(ownIntPos.y, parentIntPos.y) * 20f + 20f);
                    // rectgrid revived

                    Vector2 size = topright - leftbottom;

                    MoveSprite(rect[0], leftbottom);
                    fSprites[rect[0]].scaleY = size.y;// + size.y.Sign();
                    MoveSprite(rect[1], leftbottom);
                    fSprites[rect[1]].scaleX = size.x;// + size.x.Sign();
                    MoveSprite(rect[2], topright);
                    fSprites[rect[2]].scaleY = -size.y;// - size.y.Sign();
                    MoveSprite(rect[3], topright);
                    fSprites[rect[3]].scaleX = -size.x;// - size.x.Sign();
                    MoveSprite(rect[4], leftbottom);
                    fSprites[rect[4]].scaleX = size.x;// + size.x.Sign();
                    fSprites[rect[4]].scaleY = size.y;// + size.y.Sign();
                }
            }

            public override void SetColor(Color col)
            {
                base.SetColor(col);
                if (pixel >= 0)
                {
                    fSprites[pixel].color = col;
                }

                if (rect != null)
                {
                    for (int i = 0; i < rect.Length; i++)
                    {
                        fSprites[rect[i]].color = col;
                    }
                }
            }
        }

        public class ManagedSlider : Slider
        {
            protected readonly ManagedFieldWithPanel field;
            protected readonly IInterpolablePanelField interpolable;
            protected readonly ManagedData data;

            public ManagedSlider(ManagedFieldWithPanel field, ManagedData data, DevUINode parent, float sizeOfDisplayname) : base(parent.owner, field.key, parent, Vector2.zero, sizeOfDisplayname > 0 ? field.displayName : "", false, sizeOfDisplayname)
            {
                this.field = field;
                interpolable = field as IInterpolablePanelField;
                if (interpolable == null) throw new ArgumentException("Field must implement IInterpolablePanelField");
                this.data = data;

                DevUILabel numberLabel = subNodes[1] as DevUILabel;
                numberLabel.pos.x = sizeOfDisplayname + 10f;
                numberLabel.size.x = field.SizeOfLargestDisplayValue();
                numberLabel.fSprites[0].scaleX = numberLabel.size.x;

                // hacky hack for nubpos
                titleWidth = sizeOfDisplayname + numberLabel.size.x - 16f;
            }

            public override void NubDragged(float nubPos)
            {
                interpolable.NewFactor(this, data, nubPos);
                // this.managedControlPanel.managedRepresentation.Refresh(); // is this relevant ?
                Refresh();
            }

            public override void Refresh()
            {
                base.Refresh();
                float value = interpolable.FactorOf(this, data);
                NumberText = field.DisplayValueForNode(this, data);
                RefreshNubPos(value);
            }
        }

        public class ManagedButton : PositionedDevUINode, IDevUISignals
        {
            protected readonly Button button;
            protected readonly ManagedFieldWithPanel field;
            protected readonly IIterablePanelField iterable;
            protected readonly ManagedData data;
            public ManagedButton(ManagedFieldWithPanel field, ManagedData data, ManagedControlPanel panel, float sizeOfDisplayname) : base(panel.owner, field.key, panel, Vector2.zero)

            {
                this.field = field;
                iterable = field as IIterablePanelField;
                if (iterable == null) throw new ArgumentException("Field must implement IIterablePanelField");
                this.data = data;
                subNodes.Add(new DevUILabel(owner, "Title", this, new Vector2(0f, 0f), sizeOfDisplayname, field.displayName));
                subNodes.Add(button = new Button(owner, "Button", this, new Vector2(sizeOfDisplayname + 10f, 0f), field.SizeOfLargestDisplayValue(), field.DisplayValueForNode(this, data)));
            }

            public virtual void Signal(DevUISignalType type, DevUINode sender, string message) // from button
            {
                iterable.Next(this, data);
                Refresh();
            }

            public override void Refresh()
            {
                button.Text = field.DisplayValueForNode(this, data);
                base.Refresh();
            }
        }
        public class ManagedEnumButton : PositionedDevUINode, IDevUISignals
        {
            protected readonly Button button;
            protected readonly ManagedFieldWithPanel field;
            protected readonly ManagedData data;
            protected readonly IListablePanelField listable;
            protected readonly string[] allowedValues;
            public SelectEnumPanel enumSelectPanel;
            public ManagedEnumButton(ManagedFieldWithPanel field, ManagedData data, ManagedControlPanel panel, float sizeOfDisplayname) : base(panel.owner, field.key, panel, Vector2.zero)

            {

                this.field = field;
                listable = field as IListablePanelField;
                if (listable == null) throw new ArgumentException("Field must implement IListablePanelField");
                this.data = data;
                allowedValues = listable.list(this, data);

                subNodes.Add(button = new Button(owner, "Select_Enum_Panel_Button", this, new Vector2(10f, 0f), 150, field.displayName + field.DisplayValueForNode(this, data)));


            }

            public virtual void Signal(DevUISignalType type, DevUINode sender, string message) // from button
            {
                if (sender.IDstring == "Select_Enum_Panel_Button")
				{
					if (this.enumSelectPanel != null)
					{
						this.subNodes.Remove(this.enumSelectPanel);
						this.enumSelectPanel.ClearSprites();
						this.enumSelectPanel = null;
					}
					else
					{
                        //this.enumSelectPanel = new SelectEnumPanel(this.owner, this, button.pos + owner.room.game.cameras[0].pos, allowedValues);
                        this.enumSelectPanel = new SelectEnumPanel(this.owner, this, Vector2.zero, field.displayName, allowedValues);
                        

                        this.subNodes.Add(this.enumSelectPanel);
					}
				}
				else
				{
                    field.ParseFromText(this, data, sender.IDstring);
					for (int i = 0; i < this.subNodes.Count; i++)
					{
						if (this.subNodes[i].IDstring == "Select_Enum_Panel_Button")
						{
							(this.subNodes[i] as Button).Text = field.displayName + field.DisplayValueForNode(this, data);
						}
					}
					if (this.enumSelectPanel != null)
					{
						this.subNodes.Remove(this.enumSelectPanel);
						this.enumSelectPanel.ClearSprites();
						this.enumSelectPanel = null;
					}
				}
            }

            public override void Refresh()
            {
                button.Text = field.displayName + field.DisplayValueForNode(this, data);
                base.Refresh();
            }
        }

        public class ManagedArrowSelector : IntegerControl
        {
            protected readonly ManagedFieldWithPanel field;
            protected readonly IIterablePanelField iterable;
            protected readonly ManagedData data;

            public ManagedArrowSelector(ManagedFieldWithPanel field, ManagedData managedData, ManagedControlPanel panel, float sizeOfDisplayname) : base(panel.owner, "ManagedArrowSelector", panel, Vector2.zero, field.displayName)
            {
                this.field = field;
                iterable = field as IIterablePanelField;
                if (iterable == null) throw new ArgumentException("Field must implement IIterablePanelField");
                data = managedData;

                DevUILabel titleLabel = subNodes[0] as DevUILabel;
                titleLabel.size.x = sizeOfDisplayname;
                titleLabel.fSprites[0].scaleX = sizeOfDisplayname;

                DevUILabel numberLabel = subNodes[1] as DevUILabel;
                numberLabel.pos.x = sizeOfDisplayname + 30f;
                numberLabel.size.x = field.SizeOfLargestDisplayValue();
                numberLabel.fSprites[0].scaleX = numberLabel.size.x;

                ArrowButton arrowL = subNodes[2] as ArrowButton;
                arrowL.pos.x = sizeOfDisplayname + 10f;

                ArrowButton arrowR = subNodes[3] as ArrowButton;
                arrowR.pos.x = numberLabel.pos.x + numberLabel.size.x + 4f;
            }

            public override void Increment(int change)
            {
                if (change == 1)
                {
                    iterable.Next(this, data);
                }
                else if (change == -1)
                {
                    iterable.Prev(this, data);
                }

                Refresh();
            }

            public override void Refresh()
            {
                NumberLabelText = field.DisplayValueForNode(this, data);
                base.Refresh();
            }
        }

        public class ManagedStringControl : PositionedDevUINode
        {
            public static ManagedStringControl activeStringControl = null;

            protected readonly ManagedFieldWithPanel field;
            protected readonly ManagedData data;
            protected bool clickedLastUpdate = false;

            public ManagedStringControl(ManagedFieldWithPanel field, ManagedData data,
                    ManagedControlPanel panel, float sizeOfDisplayname)
                    : base(panel.owner, "ManagedStringControl", panel, Vector2.zero)
            {
                SetupInputDetours();

                this.field = field;
                this.data = data;

                subNodes.Add(new DevUILabel(owner, "Title", this, new Vector2(0, 0), sizeOfDisplayname, field.displayName));
                subNodes.Add(new DevUILabel(owner, "Text", this, new Vector2(60, 0), 136, ""));

                Text = field.DisplayValueForNode(this, data);

                DevUILabel textLabel = subNodes[1] as DevUILabel;
                textLabel.pos.x = sizeOfDisplayname + 10f;
                textLabel.size.x = field.SizeOfLargestDisplayValue();
                textLabel.fSprites[0].scaleX = textLabel.size.x;
            }

            private string _text;
            protected virtual string Text
            {
                get
                {
                    return _text;// subNodes[1].fLabels[0].text;
                }
                set
                {
                    _text = value;
                    subNodes[1].fLabels[0].text = value;
                }
            }

            public override void Refresh()
            {
                // No data refresh until the transaction is complete :/
                // TrySet happens on input and focus loss
                base.Refresh();
            }

            public override void Update()
            {
                if (owner.mouseClick && !clickedLastUpdate)
                {
                    if ((subNodes[1] as RectangularDevUINode).MouseOver && activeStringControl != this)
                    {
                        // replace whatever instance/null that was focused
                        activeStringControl = this;
                        subNodes[1].fLabels[0].color = new Color(0.1f, 0.4f, 0.2f);
                    }
                    else if (activeStringControl == this)
                    {
                        // focus lost
                        TrySetValue(Text, true);
                        activeStringControl = null;
                        subNodes[1].fLabels[0].color = Color.black;
                    }

                    clickedLastUpdate = true;
                }
                else if (!owner.mouseClick)
                {
                    clickedLastUpdate = false;
                }

                if (activeStringControl == this)
                {
                    foreach (char c in Input.inputString)
                    {
                        if (c == '\b')
                        {
                            if (Text.Length != 0)
                            {
                                Text = Text.Substring(0, Text.Length - 1);
                                TrySetValue(Text, false);
                            }
                        }
                        else if (c == '\n' || c == '\r')
                        {
                            // should lose focus
                            TrySetValue(Text, true);
                            activeStringControl = null;
                            subNodes[1].fLabels[0].color = Color.black;
                        }
                        else
                        {
                            Text += c;
                            TrySetValue(Text, false);
                        }
                    }
                }
            }

            protected virtual void TrySetValue(string newValue, bool endTransaction)
            {
                try
                {
                    field.ParseFromText(this, data, newValue);
                    subNodes[1].fLabels[0].color = new Color(0.1f, 0.4f, 0.2f); // positive feedback
                }
                catch (Exception)
                {
                    subNodes[1].fLabels[0].color = Color.red; // negative fedback
                }
                if (endTransaction)
                {
                    Text = field.DisplayValueForNode(this, data);
                    subNodes[1].fLabels[0].color = Color.black;
                    Refresh();
                }
            }
        }

        public class SelectEnumPanel : Panel
        {
            // copied from SelectDecalPanel, then modified to be Better
            public SelectEnumPanel(DevUI owner, DevUINode parentNode, Vector2 pos,string panelName, string[] listNames) : 
                base(
                    owner, 
                    "Select_Enum_Panel",
                    parentNode, 
                    pos, 
                    new Vector2(20f + Mathf.RoundToInt(listNames.Length / 32f + 0.5f) * 150f, (Math.Min(listNames.Length,32) * 20) + 20f), 
                    "Select " + panelName
                    )
            {
                RWCustom.IntVector2 intVector = new RWCustom.IntVector2(0, 0);
                for (int i = 0; i < listNames.Length; i++)
                {
                    subNodes.Add(new Button(owner, listNames[i], this, new Vector2(10f + Mathf.Floor(i / 32f) * 150f, size.y - 25f - 20f * (i % 32)), 145f, listNames[i]));
                    intVector.y++;
                    if (intVector.y > 33)
                    {
                        intVector.x++;
                        intVector.y = 0;
                    }
                }
                this.pos -= new Vector2(size.x, size.y / 2);
            }
        }

        #endregion CONTROLS
    }
}