using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static SuperSlopes.Utils.PlacedObjectsManager;
using UnityEngine;
using RWCustom;

namespace SuperSlopes
{

    internal class SuperSlopeObj : UpdatableAndDeletable, INotifyWhenRoomIsReady
    {
        public void AIMapReady()
        {
        }

        public void ShortcutsReady()
        {
            findIntersectingTiles();
        }

        public PlacedObject pObj;
        public Room room;
        private SuperSlopeData Data => (pObj?.data as SuperSlopeData);
        public List<IntVector2> tiles = new List<IntVector2>();
        public SuperSlopeObj(PlacedObject pObj, Room room)
        {
            this.pObj = pObj;
            this.room = room;
        }

        public void findIntersectingTiles()
        {
            Debug.Log("finding intersections");
            Debug.Log("pObj pos " + pObj.pos + " pos2 " + Data.AbsPos2);

            Data.SuperSlopeTiles = new List<IntVector2>();

            bool ceiling = Data.direction == Room.SlopeDirection.DownLeft || Data.direction == Room.SlopeDirection.DownRight;

            for (int x = 0; x < Math.Abs(Data.pos2.x)+1; x++)
            {
                for (int y = 0; y < Math.Abs(Data.pos2.y+1); y++)
                {
                    IntVector2 intTilePos = room.GetTilePosition(Data.minRect + new Vector2(x*20, y*20));

                    //don't set out of bounds tiles
                    if (intTilePos.x < 0 || intTilePos.x >= room.Tiles.GetLength(0) || intTilePos.y < 0 || intTilePos.y >= room.Tiles.GetLength(1))
                    { continue; }

                    Vector2 tilePos = room.MiddleOfTile(intTilePos);

                    if (!ceiling)
                    {
                        if (tilePos.y + 10 < Data.HeightAtPoint(tilePos.x))
                        {
                            //Debug.Log("Setting tile position " + intTilePos.ToString());
                            room.Tiles[intTilePos.x, intTilePos.y].Terrain = Room.Tile.TerrainType.Solid;
                        }

                        else if (tilePos.y + 10 >= Data.HeightAtPoint(tilePos.x) && tilePos.y < Data.HeightAtPoint(tilePos.x))
                        { 
                            room.Tiles[intTilePos.x, intTilePos.y].Terrain = SuperSlopes.EnumExt_SuperSlopes.SuperSlope;
                            Data.SuperSlopeTiles.Add(intTilePos);
                        }
                    }

                    else if (ceiling)
                    {
                        if (tilePos.y - 10 > Data.HeightAtPoint(tilePos.x))
                        {
                            //Debug.Log("Setting tile position " + intTilePos.ToString());
                            room.Tiles[intTilePos.x, intTilePos.y].Terrain = Room.Tile.TerrainType.Solid;
                        }

                        else if (tilePos.y - 10 <= Data.HeightAtPoint(tilePos.x) && tilePos.y > Data.HeightAtPoint(tilePos.x))
                        { 
                            room.Tiles[intTilePos.x, intTilePos.y].Terrain = SuperSlopes.EnumExt_SuperSlopes.SuperSlope;
                            Data.SuperSlopeTiles.Add(intTilePos);
                        }
                    }


                }
            }
        }
    }

    internal class SuperSlopeData : ManagedData
    {
        //this is a mess, lol
        internal PlacedObject pObj;

        public List<IntVector2> SuperSlopeTiles = new List<IntVector2>();

        internal IntVector2 pos2 => GetValue<IntVector2>("Line");

        internal Vector2 AbsPos2 => (pos2.ToVector2() * 20) + pObj.pos;

        internal Vector2 minRect => new Vector2(Mathf.Min(pObj.pos.x, AbsPos2.x), Mathf.Min(pObj.pos.y, AbsPos2.y));

        internal Vector2 maxRect => new Vector2(Mathf.Max(pObj.pos.x, AbsPos2.x), Mathf.Max(pObj.pos.y, AbsPos2.y));

        internal float length => Vector2.Distance(Vector2.zero, pos2.ToVector2());

        internal float angle
        {
            get
            {
                float result = (float)(Math.Atan2(pos2.y, pos2.x) * 180.0 / Math.PI);
                if (result < 0f)
                { result += 360f; }
                return result;
            }
        }

        public Room.SlopeDirection direction
        {
            get
            {
                //up left
                if (0 <= angle && angle < 90)
                { return Room.SlopeDirection.UpLeft; }
                //down left
                else if (90 <= angle && angle < 180)
                { return Room.SlopeDirection.DownLeft; }

                //down right
                else if (180 <= angle && angle < 270)
                { return Room.SlopeDirection.DownRight; }

                //up right
                else if (270 <= angle && angle < 360)
                {
                    return Room.SlopeDirection.UpRight;
                }
                return Room.SlopeDirection.Broken;

            }
        }

        public float HeightAtPoint(float x) => Vector2.Lerp(pObj.pos, AbsPos2, Mathf.InverseLerp(pObj.pos.x, AbsPos2.x, x)).y;

        public SuperSlopeData(PlacedObject po) : base(po, new ManagedField[]{new IntVector2Field("Line", new IntVector2(2, 3))})
        {
            pObj = po;
        }
    }
}
