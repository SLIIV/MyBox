using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEngine.UI;

namespace MyBox
{
    /// <summary>
    /// Conditional statement for Attributes. Does not work with DecoratorDrawers or Typed drawers.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class ConditionalFieldAttribute : PropertyAttribute
    {
        private readonly string _fieldToCheck;
        private readonly object[] _compareValues;
        private readonly bool _inverse;

        public ConditionalFieldAttribute(string fieldToCheck, bool inverse = false, params object[] compareValues)
        {
            _fieldToCheck = fieldToCheck;
            _inverse = inverse;
            _compareValues = compareValues;
        }

#if UNITY_EDITOR
        public bool CheckBehaviourPropertyVisible(MonoBehaviour behaviour, string propertyName)
        {
            if (string.IsNullOrEmpty(_fieldToCheck)) return true;

            var so = new SerializedObject(behaviour);
            var property = so.FindProperty(propertyName);

            return CheckPropertyVisible(property);
        }


        public bool CheckPropertyVisible(SerializedProperty property)
        {
            var conditionProperty = FindRelativeProperty(property, _fieldToCheck);
            if (conditionProperty == null) return true;

            string asString = AsStringValue(conditionProperty).ToUpper();

            if (_compareValues != null && _compareValues.Length > 0)
            {
                var matchAny = CompareAgainstValues(asString);
                if (_inverse) matchAny = !matchAny;
                return matchAny;
            }

            bool someValueAssigned = asString != "FALSE" && asString != "0" && asString != "NULL";
            if (someValueAssigned) return !_inverse;

            return _inverse;
        }

        /// <summary>
        /// True if the property value matches any of the values in '_compareValues'
        /// </summary>
        private bool CompareAgainstValues(string propertyValueAsString)
        {
            foreach (object valueToCompare in _compareValues)
            {
                bool valueMatches = valueToCompare.ToString().ToUpper() == propertyValueAsString;

                // One of the value is equals to the property value.
                if (valueMatches) return true;
            }

            // None of the value is equals to the property value.
            return false;
        }


        private SerializedProperty FindRelativeProperty(SerializedProperty property, string toGet)
        {
            if (property.depth == 0) return property.serializedObject.FindProperty(toGet);

            var path = property.propertyPath.Replace(".Array.data[", "[");
            var elements = path.Split('.');

            var nestedProperty = NestedPropertyOrigin(property, elements);

            // if nested property is null = we hit an array property
            if (nestedProperty == null)
            {
                var cleanPath = path.Substring(0, path.IndexOf('['));
                var arrayProp = property.serializedObject.FindProperty(cleanPath);
                if (_warningsPool.Contains(arrayProp.exposedReferenceValue)) return null;
                var target = arrayProp.serializedObject.targetObject;
                var who = string.Format("Property <color=brown>{0}</color> in object <color=brown>{1}</color> caused: ", arrayProp.name,
                    target.name);

                Debug.LogWarning(who + "Array fields is not supported by [ConditionalFieldAttribute]", target);
                _warningsPool.Add(arrayProp.exposedReferenceValue);
                return null;
            }

            return nestedProperty.FindPropertyRelative(toGet);
        }

        // For [Serialized] types with [Conditional] fields
        private SerializedProperty NestedPropertyOrigin(SerializedProperty property, string[] elements)
        {
            SerializedProperty parent = null;

            for (int i = 0; i < elements.Length - 1; i++)
            {
                var element = elements[i];
                int index = -1;
                if (element.Contains("["))
                {
                    index = Convert.ToInt32(element.Substring(element.IndexOf("[", StringComparison.Ordinal))
                        .Replace("[", "").Replace("]", ""));
                    element = element.Substring(0, element.IndexOf("[", StringComparison.Ordinal));
                }

                parent = i == 0
                    ? property.serializedObject.FindProperty(element)
                    : parent.FindPropertyRelative(element);

                if (index >= 0) parent = parent.GetArrayElementAtIndex(index);
            }

            return parent;
        }


        private string AsStringValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.String:
                    return prop.stringValue;

                case SerializedPropertyType.Character:
                case SerializedPropertyType.Integer:
                    if (prop.type == "char") return Convert.ToChar(prop.intValue).ToString();
                    return prop.intValue.ToString();

                case SerializedPropertyType.ObjectReference:
                    return prop.objectReferenceValue != null ? prop.objectReferenceValue.ToString() : "null";

                case SerializedPropertyType.Boolean:
                    return prop.boolValue.ToString();

                case SerializedPropertyType.Enum:
                    return prop.enumNames[prop.enumValueIndex];

                default:
                    return string.Empty;
            }
        }

        //This pool is used to prevent spamming with warning messages
        //One message per property
        readonly HashSet<object> _warningsPool = new HashSet<object>();
#endif
    }
}

#if UNITY_EDITOR
namespace MyBox.Internal
{
    [CustomPropertyDrawer(typeof(ConditionalFieldAttribute))]
    public class ConditionalFieldAttributeDrawer : PropertyDrawer
    {
        bool mutlipleAttributes = false;

        PropertyAttribute genericAttribute = null;
        Type genericDrawerType = null;
        PropertyDrawer genericDrawerInstance = null;

        private void GetPropertyDrawerType()
        {
            if (genericDrawerInstance == null)
            {        
                //Get the second attribute flag
                try
                {
                    genericAttribute = (PropertyAttribute)fieldInfo.GetCustomAttributes(typeof(PropertyAttribute), false)[1];

                    if (genericAttribute is DecoratorDrawer || genericAttribute is ContextMenuItemAttribute || genericAttribute is SeparatorAttribute | genericAttribute is AutoPropertyAttribute)
                    {
                        Debug.LogError(this + ": Does not work with" + genericAttribute.GetType());
                        return;
                    }
                    if (genericAttribute is TooltipAttribute) return;
                }
                catch (Exception e)
                {
                    Debug.Log(this + ": Can't find stacked propertyAttribute after ConditionalProperty: " + e);
                    return;
                }

                //Get the associated attribute drawer
                try
                {
                    Assembly[] asm = AppDomain.CurrentDomain.GetAssemblies();
                    IEnumerable<Type> t = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetTypes())
                        .Where(x => (typeof(PropertyDrawer).IsAssignableFrom(x)) && !x.IsInterface && !x.IsAbstract);
                    Type outType = t.Where(x => CustomAttributeData.GetCustomAttributes(x).First().ConstructorArguments.First().Value == genericAttribute.GetType()).First();
                    genericDrawerType = outType;
                }
                catch (Exception e)
                {
                    Debug.Log(this + ": Can't find property drawer from CustomPropertyAttribute of " + genericAttribute.GetType() + " : " + e);
                    return;
                }

                //Create instances of each (including the arguments)
                try
                {
                    genericDrawerInstance = (PropertyDrawer)Activator.CreateInstance(genericDrawerType);
                    //Get arguments
                    IList<CustomAttributeTypedArgument> attributeParams = fieldInfo.GetCustomAttributesData()[1].ConstructorArguments;
                    IList<CustomAttributeTypedArgument> unpackedParams = new List<CustomAttributeTypedArgument>();
                    //Unpack any params objec[] args
                    foreach (CustomAttributeTypedArgument singleParam in attributeParams)
                    {
                        if (singleParam.Value.GetType() == typeof(ReadOnlyCollection<CustomAttributeTypedArgument>))
                        {
                            foreach (CustomAttributeTypedArgument unpackedSingleParam in (ReadOnlyCollection<CustomAttributeTypedArgument>)singleParam.Value)
                            {
                                unpackedParams.Add(unpackedSingleParam);
                            }
                        }
                        else
                        {
                            unpackedParams.Add(singleParam);
                        }
                    }
                    object[] attributeParamsObj = (object[])unpackedParams.Select(x => x.Value).Cast<object>().ToArray();

                    if (attributeParamsObj.Count() > 0)
                    {
                        genericAttribute = (PropertyAttribute)Activator.CreateInstance(genericAttribute.GetType(), attributeParamsObj);
                    }
                    else
                    {
                        genericAttribute = (PropertyAttribute)Activator.CreateInstance(genericAttribute.GetType());
                    }
                }
                catch (Exception e)
                {
                    Debug.Log(this + ": no constructor available in " + genericAttribute.GetType() + " : " + e);
                    return;
                }

                //Reassign the attribute field in the drawer so it can access the argument values
                try
                {
                    genericDrawerType.GetField("m_Attribute", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(genericDrawerInstance, genericAttribute);
                }
                catch (Exception e)
                {
                    Debug.Log(this + ": Unable to assign attribute to " + genericDrawerInstance.GetType() + " : " + e);
                    return;
                }
            }
        }

        private ConditionalFieldAttribute Attribute
        {
            get { return _attribute ?? (_attribute = attribute as ConditionalFieldAttribute); }
        }

        private ConditionalFieldAttribute _attribute;

        private bool _toShow = true;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (fieldInfo.GetCustomAttributes(typeof(PropertyAttribute), false).Count() > 1)
            {
                mutlipleAttributes = true;
                GetPropertyDrawerType();
            }
            _toShow = Attribute.CheckPropertyVisible(property);
            if (_toShow)
            {
                if (genericDrawerInstance != null)
                {
                    return genericDrawerInstance.GetPropertyHeight(property, label);
                }
                else
                {
                    return EditorGUI.GetPropertyHeight(property);
                }
            }
            else
            {
                return 0;
            }
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (_toShow)
            {
                if (mutlipleAttributes && genericDrawerInstance != null)
                {
                    try
                    {
                        genericDrawerInstance.OnGUI(position, property, label);
                    }
                    catch (Exception e)
                    {
                        EditorGUI.PropertyField(position, property, label);
                        Debug.Log(this + ": Unable to instantiate " + genericAttribute.GetType() + " : " + e);
                    }
                }
                else
                {
                    EditorGUI.PropertyField(position, property, label);
                }
            }
        }
    }
}
#endif