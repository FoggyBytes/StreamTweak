// Ported verbatim from NVIDIA Profile Inspector (MIT, © Orbmu2k).
//   nvidiaProfileInspector/Common/Helper/XMLHelper.cs
// Only the namespace changed (→ StreamTweak.Nvidia.Helper).
// Serializes/deserializes the .nip XML (UTF-16, no namespaces) so the files
// are byte-compatible with NVIDIA Profile Inspector itself.
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace StreamTweak.Nvidia.Helper
{
    public static class XMLHelper<T> where T : new()
    {
        static XmlSerializer xmlSerializer;

        static XMLHelper()
        {
            xmlSerializer = new XmlSerializer(typeof(T));
        }

        public static string SerializeToXmlString(T xmlObject, Encoding encoding, bool removeNamespace)
        {
            var memoryStream = new MemoryStream();
            var xmlWriter = new XmlTextWriter(memoryStream, encoding) { Formatting = Formatting.Indented };

            if (removeNamespace)
            {
                var xs = new XmlSerializerNamespaces();
                xs.Add("", "");
                xmlSerializer.Serialize(xmlWriter, xmlObject, xs);
            }
            else
                xmlSerializer.Serialize(xmlWriter, xmlObject);

            return encoding.GetString(memoryStream.ToArray());
        }

        public static void SerializeToXmlFile(T xmlObject, string filename, Encoding encoding, bool removeNamespace)
        {
            File.WriteAllText(filename, SerializeToXmlString(xmlObject, encoding, removeNamespace));
        }

        public static T DeserializeFromXmlString(string xml)
        {
            var reader = new StringReader(xml);
            var xmlObject = (T)xmlSerializer.Deserialize(reader);
            return xmlObject;
        }

        public static T DeserializeFromXMLFile(string filename)
        {
            return DeserializeFromXmlString(File.ReadAllText(filename));
        }

    }

}
