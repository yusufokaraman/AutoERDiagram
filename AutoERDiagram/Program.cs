using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace AutoERDiagram
{
    public class Program
    {
        // Veritabanı bağlantı bilgilerinizi buraya uyarlayın
        private const string ConnectionString =
            "Server=10.35.36.3;Initial Catalog=ServerManagement;Connect Timeout=30;" +
            "Encrypt=False;User Id=sa;Password=Abc123def!!!;" +
            "TrustServerCertificate=False;ApplicationIntent=ReadWrite;MultiSubnetFailover=False;MultipleActiveResultSets=true;";

        // Çıktı dosyalarının kaydedileceği dizin:
        // Burasını istediğiniz klasörle güncelleyebilirsiniz.
        private static readonly string outputDirectory =
            @"D:\projects\ykaraman\other_apps\AutoERDiagram\AutoERDiagram\bin\Debug\net8.0";

        // Graphviz dot.exe tam yolu (Kurulumunuza göre güncelleyin!)
        private static readonly string graphvizDotPath =
            @"C:\Program Files\Graphviz\bin\dot.exe";

        public static void Main(string[] args)
        {
            // 1) Tablolar, kolonlar ve ForeignKey ilişkilerini çek
            var tables = GetDatabaseSchema();

            // 2) Graphviz (DOT) formatında bir metin oluştur
            string dotContent = GenerateDotFileContent(tables);

            // 3) diagram.dot dosyasına yaz
            //    Tam yolu net olsun diye outputDirectory + "diagram.dot"
            string dotFilePath = Path.Combine(outputDirectory, "diagram.dot");
            File.WriteAllText(dotFilePath, dotContent, new System.Text.UTF8Encoding(false));

            Console.WriteLine($"DOT dosyası oluşturuldu: {dotFilePath}");
            Console.WriteLine("DOT dosyası tam yol: " + Path.GetFullPath(dotFilePath));

            // 4) .dot'u .png'ye dönüştür
            string pngFilePath = Path.Combine(outputDirectory, "diagram.png");
            ConvertDotToPng(dotFilePath, pngFilePath);

            Console.WriteLine("İşlem tamamlandı. Enter ile çıkabilirsiniz.");
            Console.ReadLine();
        }

        /// <summary>
        /// sys.tables, sys.columns, sys.foreign_keys vb. üzerinden tablo + kolon + FK bilgisi alır
        /// </summary>
        private static List<TableModel> GetDatabaseSchema()
        {
            var tables = new List<TableModel>();
            var columnsDict = new Dictionary<int, List<ColumnModel>>();
            var foreignKeys = new List<ForeignKeyModel>();

            using (var conn = new SqlConnection(ConnectionString))
            {
                conn.Open();

                // 1) Tabloları çek
                using (var cmd = new SqlCommand(@"
                    SELECT t.object_id, t.name 
                    FROM sys.tables t
                    ORDER BY t.name
                ", conn))
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        var table = new TableModel
                        {
                            ObjectId = rdr.GetInt32(0),
                            TableName = rdr.GetString(1)
                        };
                        tables.Add(table);
                    }
                }

                // 2) Kolonları çek
                using (var cmd = new SqlCommand(@"
                    SELECT c.object_id, c.name, ty.name, c.max_length, c.is_nullable
                    FROM sys.columns c
                    JOIN sys.types ty ON c.user_type_id = ty.user_type_id
                    ORDER BY c.object_id, c.column_id
                ", conn))
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        int objId = rdr.GetInt32(0);
                        string colName = rdr.GetString(1);
                        string dataType = rdr.GetString(2);
                        short maxLength = rdr.GetInt16(3);
                        bool isNullable = rdr.GetBoolean(4);

                        if (!columnsDict.ContainsKey(objId))
                            columnsDict[objId] = new List<ColumnModel>();

                        columnsDict[objId].Add(new ColumnModel
                        {
                            ObjectId = objId,
                            ColumnName = colName,
                            DataType = dataType,
                            MaxLength = maxLength,
                            IsNullable = isNullable
                        });
                    }
                }

                // 3) Foreign key ilişkileri çek
                using (var cmd = new SqlCommand(@"
                    SELECT fk.name AS FKName,
                           OBJECT_NAME(fk.parent_object_id) AS FKTable,
                           OBJECT_NAME(fk.referenced_object_id) AS PKTable,
                           c1.name AS FKColumnName,
                           c2.name AS PKColumnName
                    FROM sys.foreign_keys fk
                    INNER JOIN sys.foreign_key_columns fkc 
                            ON fk.object_id = fkc.constraint_object_id
                    INNER JOIN sys.columns c1 
                            ON fkc.parent_object_id = c1.object_id 
                            AND fkc.parent_column_id = c1.column_id
                    INNER JOIN sys.columns c2 
                            ON fkc.referenced_object_id = c2.object_id 
                            AND fkc.referenced_column_id = c2.column_id
                ", conn))
                using (var rdr = cmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        var fkModel = new ForeignKeyModel
                        {
                            FKName = rdr.GetString(0),
                            FKTable = rdr.GetString(1),
                            PKTable = rdr.GetString(2),
                            FKColumnName = rdr.GetString(3),
                            PKColumnName = rdr.GetString(4)
                        };
                        foreignKeys.Add(fkModel);
                    }
                }
            }

            // 4) Tablolara kolon ve FK ekleme
            foreach (var table in tables)
            {
                // Kolonları ekle
                if (columnsDict.TryGetValue(table.ObjectId, out var cols))
                {
                    table.Columns.AddRange(cols);
                }

                // Bu tabloya ait foreign key'leri ekle
                var relatedFks = foreignKeys
                    .Where(fk => fk.FKTable.Equals(table.TableName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                table.ForeignKeys.AddRange(relatedFks);
            }

            return tables;
        }

        /// <summary>
        /// Graphviz'in .dot formatında bir çıktı oluşturur.
        /// Her tablo bir node, aralarındaki FK'lar edge olarak gösterilir.
        /// </summary>
        private static string GenerateDotFileContent(List<TableModel> tables)
        {
            var sb = new StringBuilder();

            sb.AppendLine("digraph DatabaseDiagram {");
            sb.AppendLine("  // Daha kompakt bir layout için graph ayarları:");
            sb.AppendLine("  graph [");
            sb.AppendLine("    // Düğümlerin üst üste binmesini önle ve kompaktlaştır");
            sb.AppendLine("    overlap=false,");

            sb.AppendLine("    // ranksep = satırlar (veya sütunlar) arası mesafe, nodesep = yanyana node aralığı");
            sb.AppendLine("    ranksep=0.4,");
            sb.AppendLine("    nodesep=0.3,");

            sb.AppendLine("    // DPI ile diyagramın çözünürlüğünü artırabilirsiniz");
            sb.AppendLine("    dpi=200,");

            sb.AppendLine("    // margin diyagram kenar boşluğu (istemiyorsanız küçültebilirsiniz)");
            sb.AppendLine("    margin=0.2");

            sb.AppendLine("  ];");

            // soldan sağa çiz (isteğe bağlı)
            sb.AppendLine("  rankdir=LR;");

            // varsayılan node ayarları (fontsize, fontname vb.)
            sb.AppendLine("  node [shape=none, fontsize=12, fontname=\"Arial\"];");

            // Her tabloyu bir node olarak tanımlayalım
            foreach (var table in tables)
            {
                sb.Append($"  \"{table.TableName}\" [label=<");
                sb.Append("<table border=\"1\" cellborder=\"0\" cellspacing=\"0\" cellpadding=\"4\">");
                sb.Append($"<tr><td colspan=\"2\" bgcolor=\"#D3D3D3\"><b>{table.TableName}</b></td></tr>");

                foreach (var col in table.Columns)
                {
                    sb.Append("<tr>");
                    sb.Append($"<td align=\"left\" port=\"{col.ColumnName}\">{col.ColumnName}</td>");
                    sb.Append($"<td align=\"left\">{col.DataType}");

                    if (col.MaxLength > 0 &&
                        col.DataType != "int" &&
                        col.DataType != "bigint" &&
                        col.DataType != "uniqueidentifier")
                    {
                        sb.Append($"({col.MaxLength})");
                    }

                    if (!col.IsNullable)
                    {
                        sb.Append(" NOT NULL");
                    }

                    sb.Append("</td>");
                    sb.Append("</tr>");
                }

                sb.Append("</table>");
                sb.Append(">];");
                sb.AppendLine();
            }

            // Foreign key ilişkilerini çizen kısım
            foreach (var table in tables)
            {
                foreach (var fk in table.ForeignKeys)
                {
                    sb.AppendLine($"  \"{fk.FKTable}\" -> \"{fk.PKTable}\" [label=\"{fk.FKColumnName}\"];");
                }
            }

            sb.AppendLine("}");
            return sb.ToString();
        }


        /// <summary>
        /// diagram.dot dosyasını Graphviz kullanarak diagram.png'ye dönüştürür
        /// (Sistemde 'dot.exe' kurulu ve graphvizDotPath doğru ayarlı olmalı)
        /// </summary>
        private static void ConvertDotToPng(string dotPath, string outputPng)
        {
            try
            {
                // Eğer PATH'e ekli ise sadece "dot" da diyebilirsiniz,
                // ama burada tam yol kullanıyoruz.
                var startInfo = new ProcessStartInfo(graphvizDotPath)
                {
                    //Arguments = $"-Tpng \"{dotPath}\" -o \"{outputPng}\"",
                    Arguments = $"-Tpng -Gdpi=200 \"{dotPath}\" -o \"{outputPng}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using (var proc = new Process { StartInfo = startInfo })
                {
                    proc.Start();

                    // Komut çalışırken gelecek tüm çıktıyı okuyalım
                    string output = proc.StandardOutput.ReadToEnd();
                    string error = proc.StandardError.ReadToEnd();

                    proc.WaitForExit();

                    // Komut satırı çıktılarını ekrana basalım (hata var mı görelim)
                    if (!string.IsNullOrEmpty(output))
                        Console.WriteLine("dot output: " + output);
                    if (!string.IsNullOrEmpty(error))
                        Console.WriteLine("dot error: " + error);

                    Console.WriteLine("dot exit code: " + proc.ExitCode);

                    // Başarılı sonuç
                    if (proc.ExitCode == 0)
                    {
                        Console.WriteLine($"PNG diyagram oluşturuldu: {outputPng}");
                        Console.WriteLine("PNG diyagram tam yol: " + Path.GetFullPath(outputPng));
                    }
                    else
                    {
                        Console.WriteLine("dot komutu hata ile sonuçlandı, PNG oluşmadı.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Graphviz (dot) komutu çalıştırılamadı:");
                Console.WriteLine(ex.Message);
            }
        }
    }

    /// <summary>
    /// Tabloyu temsil eden model
    /// </summary>
    public class TableModel
    {
        public int ObjectId { get; set; }
        public string TableName { get; set; } = string.Empty;
        public List<ColumnModel> Columns { get; set; } = new();
        public List<ForeignKeyModel> ForeignKeys { get; set; } = new();
    }

    /// <summary>
    /// Tablo kolonlarını temsil eden model
    /// </summary>
    public class ColumnModel
    {
        public int ObjectId { get; set; }
        public string ColumnName { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public int MaxLength { get; set; }
        public bool IsNullable { get; set; }
    }

    /// <summary>
    /// Foreign key ilişkisini temsil eden model
    /// </summary>
    public class ForeignKeyModel
    {
        public string FKName { get; set; } = string.Empty;
        public string FKTable { get; set; } = string.Empty;
        public string FKColumnName { get; set; } = string.Empty;
        public string PKTable { get; set; } = string.Empty;
        public string PKColumnName { get; set; } = string.Empty;
    }
}
