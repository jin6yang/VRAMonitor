using System;
using System.Collections.Generic;
using System.Text;

namespace VRAMonitor.Helpers
{
    public class GroupInfoList : List<object>
    {
        public GroupInfoList(IEnumerable<object> items) : base(items)
        {
        }
        public object Key { get; set; }

        public override string ToString()
        {
            return "Group " + Key.ToString();
        }
    }
}
